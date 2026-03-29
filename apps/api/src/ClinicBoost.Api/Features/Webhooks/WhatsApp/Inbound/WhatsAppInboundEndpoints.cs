using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Webhooks;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

// ════════════════════════════════════════════════════════════════════════════
// WhatsAppInboundEndpoints  —  POST /webhooks/twilio/whatsapp
//
// RESPONSABILIDADES DE ESTE ENDPOINT (SÓLO)
// ──────────────────────────────────────────
// 1. Leer el body como form (ReadFormAsync). Sin buffering adicional.
// 2. Construir la URL de validación (usa WebhookBaseUrl o scheme/host).
// 3. Validar la firma X-Twilio-Signature (HMAC-SHA1). → 403 si inválida.
// 4. Parsear campos mínimos del webhook.
// 5. Resolver el tenant por el número de la clínica (To sin prefijo WA).
//    → 200 TwiML vacío si no se encuentra (evita reintentos de Twilio).
// 6. Registrar idempotencia con eventType="twilio.whatsapp_inbound"
//    y eventId=MessageSid. → 200 inmediato si duplicado.
// 7. Persistir WebhookEvent (trazabilidad, payload crudo, correlación).
// 8. Encolar WhatsAppInboundJob en el Channel (fire-and-forget).
// 9. Responder 200 con TwiML vacío en < 5 s (exigido por Twilio).
//
// LO QUE NO HACE ESTE ENDPOINT
// ─────────────────────────────
// · NO crea pacientes ni busca en la BD de negocio.
// · NO crea ni actualiza Conversation/Message.
// · NO llama a ningún agente IA.
// · Todo lo pesado se delega al WhatsAppInboundWorker.
//
// SEGURIDAD
// ─────────
// · AllowAnonymous: los webhooks de Twilio no llevan JWT.
//   La autenticación es la validación de firma HMAC-SHA1.
// · 403 en firma inválida (no 200: no revelar que el endpoint existe).
// · 200 + TwiML vacío en tenant no encontrado (evita reintentos de Twilio).
//
// IDEMPOTENCIA
// ─────────────
// · Clave: ("twilio.whatsapp_inbound", MessageSid, tenantId)
// · Si Twilio re-entrega el mismo MessageSid → ya existe en processed_events
//   → 200 inmediato sin re-encolar.
// · Payload hash detecta re-entregas alteradas (posible replay attack).
// ════════════════════════════════════════════════════════════════════════════

public static class WhatsAppInboundEndpoints
{
    public static IEndpointRouteBuilder MapWhatsAppInboundEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/twilio/whatsapp", HandleAsync)
           .WithName("TwilioWhatsAppInboundWebhook")
           .WithTags("Webhooks", "Twilio", "WhatsApp")
           .WithSummary("Webhook WhatsApp inbound de Twilio — recepción de mensajes")
           .WithDescription(
               "Recibe mensajes WhatsApp inbound de Twilio. " +
               "Valida la firma HMAC-SHA1, registra idempotencia y encola el job.")
           .AllowAnonymous()        // autenticación = firma Twilio, no JWT
           .DisableAntiforgery();   // form POST desde Twilio, no browser

        return app;
    }

    // ── Handler ───────────────────────────────────────────────────────────────

    private static async Task<IResult> HandleAsync(
        HttpContext                    ctx,
        ITwilioSignatureValidator      signatureValidator,
        ITenantPhoneResolver           phoneResolver,
        IIdempotencyService            idempotency,
        IWhatsAppJobQueue              jobQueue,
        AppDbContext                   db,
        IOptions<TwilioOptions>        twilioOptions,
        ILoggerFactory                 loggerFactory,
        CancellationToken              ct)
    {
        var logger  = loggerFactory.CreateLogger("ClinicBoost.Webhooks.WhatsApp");
        var request = ctx.Request;

        // ── 1. Leer body como form ────────────────────────────────────────────
        var form = await request.ReadFormAsync(ct);

        // ── 2. Construir URL de validación ────────────────────────────────────
        var opts    = twilioOptions.Value;
        var baseUrl = string.IsNullOrWhiteSpace(opts.WebhookBaseUrl)
            ? $"{request.Scheme}://{request.Host}"
            : opts.WebhookBaseUrl.TrimEnd('/');
        var webhookUrl = $"{baseUrl}{request.Path}{request.QueryString}";

        // ── 3. Validar firma X-Twilio-Signature ───────────────────────────────
        var twilioSig = request.Headers["X-Twilio-Signature"].ToString();

        if (!signatureValidator.IsValid(
                TwilioWhatsAppRequest.ToSignatureParams(form),
                webhookUrl,
                twilioSig))
        {
            logger.LogWarning(
                "[WAInbound] Firma inválida rechazada. " +
                "IP={IP} UserAgent={UA} URL={URL}",
                ctx.Connection.RemoteIpAddress,
                request.Headers.UserAgent.ToString(),
                webhookUrl);

            return Results.StatusCode(403);
        }

        // ── 4. Parsear campos del webhook ─────────────────────────────────────
        var waReq = TwilioWhatsAppRequest.FromForm(form);

        if (string.IsNullOrWhiteSpace(waReq.MessageSid) ||
            string.IsNullOrWhiteSpace(waReq.To))
        {
            logger.LogWarning(
                "[WAInbound] Webhook con campos obligatorios vacíos. " +
                "MessageSid='{Sid}' To='{To}'",
                waReq.MessageSid, waReq.To);
            return TwimlOk();
        }

        // ── 5. Resolver tenant por número de la clínica ───────────────────────
        // ClinicPhone ya tiene el prefijo "whatsapp:" eliminado.
        var tenantId = await phoneResolver.ResolveAsync(waReq.ClinicPhone, ct);

        if (!tenantId.HasValue)
        {
            logger.LogWarning(
                "[WAInbound] Número no asociado a ningún tenant activo. " +
                "To={To} ClinicPhone={Phone} MessageSid={Sid}",
                waReq.To, waReq.ClinicPhone, waReq.MessageSid);
            return TwimlOk();
        }

        // ── 6. Payload para hash de idempotencia ──────────────────────────────
        var rawPayload = string.Concat(
            form.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}&"));

        // ── 7. Idempotencia ───────────────────────────────────────────────────
        var idemResult = await idempotency.TryProcessAsync(
            eventType: "twilio.whatsapp_inbound",
            eventId:   waReq.MessageSid,
            tenantId:  tenantId,
            payload:   rawPayload,
            ct:        ct);

        if (idemResult.IsError)
        {
            logger.LogError(
                "[WAInbound] Error de idempotencia. " +
                "MessageSid={Sid} TenantId={TenantId}. " +
                "Devolviendo 500 para que Twilio reintente.",
                waReq.MessageSid, tenantId);
            return Results.StatusCode(500);
        }

        if (idemResult.IsPayloadMismatch)
        {
            logger.LogCritical(
                "[WAInbound] ALERTA: Payload hash mismatch para MessageSid={Sid} " +
                "TenantId={TenantId}. Posible replay attack. Ignorando.",
                waReq.MessageSid, tenantId);
            return TwimlOk();
        }

        if (idemResult.AlreadyProcessed)
        {
            logger.LogDebug(
                "[WAInbound] Evento duplicado ignorado. " +
                "MessageSid={Sid} TenantId={TenantId} " +
                "FirstProcessedAt={FirstProcessedAt}",
                waReq.MessageSid, tenantId, idemResult.FirstProcessedAt);
            return TwimlOk();
        }

        // ── 8. Persistir WebhookEvent (trazabilidad) ──────────────────────────
        // Cabecera de firma truncada a 8 chars + "…" para no guardar el token completo.
        var sigHeader = twilioSig.Length > 8
            ? $"{{\"X-Twilio-Signature\":\"{twilioSig[..8]}…\"}}"
            : $"{{\"X-Twilio-Signature\":\"{twilioSig}\"}}";

        var webhookEvent = new WebhookEvent
        {
            TenantId       = tenantId,
            Source         = "twilio",
            EventType      = "whatsapp_inbound",
            Payload        = rawPayload,
            Headers        = sigHeader,
            Status         = "pending",
            IdempotencyKey = idemResult.ProcessedEventId?.ToString(),
            CorrelationId  = Guid.TryParse(ctx.TraceIdentifier, out var traceGuid)
                                 ? traceGuid
                                 : Guid.NewGuid()
        };
        db.WebhookEvents.Add(webhookEvent);
        await db.SaveChangesAsync(ct);

        // ── 9. Encolar job (fire-and-forget) ──────────────────────────────────
        var job = new WhatsAppInboundJob(
            TenantId:         tenantId.Value,
            MessageSid:       waReq.MessageSid,
            CallerPhone:      waReq.CallerPhone,
            ClinicPhone:      waReq.ClinicPhone,
            Body:             waReq.Body,
            MediaUrl:         waReq.MediaUrl0,
            MediaType:        waReq.MediaContentType0,
            ProfileName:      waReq.ProfileName,
            ReceivedAt:       DateTimeOffset.UtcNow,
            ProcessedEventId: idemResult.ProcessedEventId!.Value,
            CorrelationId:    ctx.TraceIdentifier);

        var enqueued = await jobQueue.EnqueueAsync(job, ct);

        if (!enqueued)
        {
            logger.LogWarning(
                "[WAInbound] Job no encolado (cola llena). " +
                "MessageSid={Sid} TenantId={TenantId}. " +
                "El evento ya está en processed_events; Twilio NO reintentará.",
                waReq.MessageSid, tenantId);
        }

        logger.LogInformation(
            "[WAInbound] Mensaje procesado. " +
            "MessageSid={Sid} TenantId={TenantId} " +
            "Caller={Caller} Enqueued={Enqueued}",
            waReq.MessageSid, tenantId, waReq.CallerPhone, enqueued);

        // ── 10. Responder con TwiML vacío ─────────────────────────────────────
        // Twilio no espera respuesta automática para mensajes entrantes WA;
        // un TwiML vacío confirma la recepción sin enviar nada.
        return TwimlOk();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Respuesta TwiML vacía. Para WhatsApp confirma recepción sin enviar mensaje.
    /// </summary>
    private static IResult TwimlOk() =>
        Results.Content(
            content:     "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response></Response>",
            contentType: "text/xml; charset=utf-8");
}
