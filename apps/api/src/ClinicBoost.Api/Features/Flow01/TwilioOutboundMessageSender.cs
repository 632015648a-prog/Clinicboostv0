using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Variants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// TwilioOutboundMessageSender
//
// Implementación de IOutboundMessageSender para Twilio.
//
// FLUJO DE ENVÍO
// ──────────────
//  1. Upsert de Conversation (estado active) si no se provee ConversationId.
//  2. Crear Message en BD con status="pending" (el registro existe antes del
//     envío para garantizar trazabilidad aunque Twilio falle).
//  3. POST a https://api.twilio.com/2010-04-01/Accounts/{SID}/Messages.json
//     con Basic Auth (AccountSid:AuthToken) — credentials SOLO en backend.
//  4. Actualizar Message.Status = "sent" + ProviderMessageId = TwilioSid.
//  5. En caso de error: actualizar Message.Status = "failed", rellenar
//     ErrorCode/ErrorMessage, devolver OutboundSendResult.TwilioFailure.
//
// FORMATO DEL NÚMERO
// ──────────────────
//  · WhatsApp: "whatsapp:+34600000000"
//  · SMS:      "+34600000000"
//  El caller es responsable de pasar el prefijo correcto en FromPhone/ToPhone.
//
// IDEMPOTENCIA
// ────────────
//  · El Message en BD se crea ANTES de llamar a Twilio.
//  · Si la respuesta de Twilio llega pero el UPDATE falla, el siguiente intento
//    de envío creará un nuevo Message (el caller evita dobles envíos con
//    IIdempotencyService antes de llamar a este sender).
//
// CREDENCIALES
// ────────────
//  · AccountSid y AuthToken solo se leen de TwilioOptions (DI).
//  · NUNCA se aceptan del request HTTP ni del frontend.
//  · Para producción: TWILIO__ACCOUNTSID y TWILIO__AUTHTOKEN en variables de entorno.
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="IOutboundMessageSender"/> sobre Twilio REST API.
/// </summary>
public sealed class TwilioOutboundMessageSender : IOutboundMessageSender
{
    private readonly AppDbContext                          _db;
    private readonly TwilioOptions                        _opts;
    private readonly IHttpClientFactory                   _httpFactory;
    private readonly IVariantTrackingService              _variantTracking;
    private readonly ILogger<TwilioOutboundMessageSender> _logger;

    // Nombre del HttpClient registrado en DI para la Twilio API
    private const string TwilioHttpClient = "Twilio";

    public TwilioOutboundMessageSender(
        AppDbContext                          db,
        IOptions<TwilioOptions>              opts,
        IHttpClientFactory                   httpFactory,
        IVariantTrackingService              variantTracking,
        ILogger<TwilioOutboundMessageSender> logger)
    {
        _db              = db;
        _opts            = opts.Value;
        _httpFactory     = httpFactory;
        _variantTracking = variantTracking;
        _logger          = logger;
    }

    // ── SendAsync ─────────────────────────────────────────────────────────────

    public async Task<OutboundSendResult> SendAsync(
        OutboundMessageRequest request,
        CancellationToken      ct = default)
    {
        _logger.LogInformation(
            "[OutboundSender] Iniciando envío. To={To} Channel={Channel} Flow={Flow} " +
            "CorrelationId={CorrelationId}",
            request.ToPhone, request.Channel, request.FlowId, request.CorrelationId);

        // ── 1. Upsert Conversation ────────────────────────────────────────────
        var conversationId = request.ConversationId
            ?? await UpsertConversationAsync(request, ct);

        // ── 2. Crear Message con status=pending ───────────────────────────────
        var message = new Message
        {
            TenantId           = request.TenantId,
            ConversationId     = conversationId,
            Direction          = "outbound",
            Channel            = request.Channel,
            Body               = request.Body,
            TemplateId         = request.TemplateSid,
            TemplateVars       = request.TemplateVars,
            Status             = "pending",
            GeneratedByAi      = false,
            // Propagar variante A/B seleccionada por el orchestrator
            MessageVariantId   = request.MessageVariantId,
        };
        _db.Messages.Add(message);
        await _db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "[OutboundSender] Message creado en BD. MessageId={MsgId} VariantId={VarId} Status=pending",
            message.Id, request.MessageVariantId);

        // ── 3. Llamar a Twilio ────────────────────────────────────────────────
        try
        {
            var twilioSid = await CallTwilioAsync(request, ct);

            // ── 4. Actualizar a sent ──────────────────────────────────────────
            message.Status             = "sent";
            message.ProviderMessageId  = twilioSid;
            message.SentAt             = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[OutboundSender] Mensaje enviado OK. MessageId={MsgId} TwilioSid={Sid}",
                message.Id, twilioSid);

            // ── Registrar evento outbound_sent en funnel de variante ──────────
            if (request.MessageVariantId.HasValue)
            {
                await _variantTracking.RecordEventAsync(new VariantConversionEvent
                {
                    TenantId          = request.TenantId,
                    MessageVariantId  = request.MessageVariantId.Value,
                    MessageId         = message.Id,
                    ConversationId    = conversationId,
                    ProviderMessageId = twilioSid,
                    EventType         = VariantEventType.OutboundSent,
                    ElapsedMs         = null,   // evento base del funnel
                    CorrelationId     = request.CorrelationId,
                    Metadata          = JsonSerializer.Serialize(new
                    {
                        channel      = request.Channel,
                        flow_id      = request.FlowId,
                        template_sid = request.TemplateSid,
                    }),
                }, ct);
            }

            return OutboundSendResult.Success(message.Id, twilioSid);
        }
        catch (TwilioApiException tex)
        {
            // ── 5. Twilio devolvió error ──────────────────────────────────────
            message.Status       = "failed";
            message.ErrorCode    = tex.TwilioCode;
            message.ErrorMessage = tex.Message;
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "[OutboundSender] Twilio error. MessageId={MsgId} Code={Code} Error={Error}",
                message.Id, tex.TwilioCode, tex.Message);

            return OutboundSendResult.TwilioFailure(message.Id, tex.TwilioCode, tex.Message);
        }
        catch (Exception ex)
        {
            // Error inesperado (red, timeout, etc.)
            message.Status       = "failed";
            message.ErrorCode    = "network_error";
            message.ErrorMessage = ex.Message;
            await _db.SaveChangesAsync(ct);

            _logger.LogError(ex,
                "[OutboundSender] Error inesperado. MessageId={MsgId} To={To}",
                message.Id, request.ToPhone);

            return OutboundSendResult.TwilioFailure(message.Id, "network_error", ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> UpsertConversationAsync(
        OutboundMessageRequest request,
        CancellationToken      ct)
    {
        var activeStatuses = new[] { "open", "waiting_ai", "waiting_human" };

        var existing = await _db.Conversations
            .Where(c =>
                c.TenantId  == request.TenantId  &&
                c.PatientId == request.PatientId  &&
                c.Channel   == request.Channel    &&
                c.FlowId    == request.FlowId     &&
                activeStatuses.Contains(c.Status))
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
            return existing.Id;

        var newConv = new Conversation
        {
            TenantId         = request.TenantId,
            PatientId        = request.PatientId,
            Channel          = request.Channel,
            FlowId           = request.FlowId,
            Status           = "open",
            SessionExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        };
        _db.Conversations.Add(newConv);
        await _db.SaveChangesAsync(ct);

        return newConv.Id;
    }

    /// <summary>
    /// Llama a la Twilio Messages API y devuelve el MessageSid.
    /// Lanza <see cref="TwilioApiException"/> si Twilio responde con error.
    /// </summary>
    private async Task<string> CallTwilioAsync(
        OutboundMessageRequest request,
        CancellationToken      ct)
    {
        var url  = $"2010-04-01/Accounts/{_opts.AccountSid}/Messages.json";
        var form = BuildFormContent(request);

        var client = _httpFactory.CreateClient(TwilioHttpClient);

        // Basic Auth: AccountSid:AuthToken (credentials solo en backend)
        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{_opts.AccountSid}:{_opts.AuthToken}"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);

        using var response = await client.PostAsync(url, form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Intentar extraer código de error de Twilio del JSON
            string twilioCode   = ((int)response.StatusCode).ToString();
            string twilioMessage = body;
            try
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("code", out var codeEl))
                    twilioCode = codeEl.GetInt32().ToString();
                if (json.RootElement.TryGetProperty("message", out var msgEl))
                    twilioMessage = msgEl.GetString() ?? body;
            }
            catch { /* JSON inválido; usar el body raw */ }

            throw new TwilioApiException(twilioCode, twilioMessage);
        }

        // Extraer el MessageSid de la respuesta JSON
        var responseJson = JsonDocument.Parse(body);
        if (!responseJson.RootElement.TryGetProperty("sid", out var sidEl))
            throw new TwilioApiException("missing_sid",
                "La respuesta de Twilio no contiene 'sid'.");

        return sidEl.GetString()
            ?? throw new TwilioApiException("null_sid", "Twilio devolvió sid=null.");
    }

    private static FormUrlEncodedContent BuildFormContent(OutboundMessageRequest request)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("To",   request.ToPhone),
            new("From", request.FromPhone),
        };

        if (!string.IsNullOrEmpty(request.TemplateSid))
        {
            // Content Template aprobada
            fields.Add(new("ContentSid", request.TemplateSid));
            if (!string.IsNullOrEmpty(request.TemplateVars))
                fields.Add(new("ContentVariables", request.TemplateVars));
        }
        else if (!string.IsNullOrEmpty(request.Body))
        {
            fields.Add(new("Body", request.Body));
        }

        return new FormUrlEncodedContent(fields);
    }
}

// ── Excepción tipada de Twilio ────────────────────────────────────────────────

/// <summary>
/// Error de la Twilio API. Encapsula el código numérico de Twilio y el mensaje.
/// No debe propagarse fuera de TwilioOutboundMessageSender.
/// </summary>
internal sealed class TwilioApiException : Exception
{
    public string TwilioCode { get; }

    public TwilioApiException(string code, string message)
        : base(message)
    {
        TwilioCode = code;
    }
}
