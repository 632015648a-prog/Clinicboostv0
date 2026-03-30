using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Webhooks;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;

// ════════════════════════════════════════════════════════════════════════════
// MessageStatusEndpoints  —  POST /webhooks/twilio/message-status
//
// RESPONSABILIDADES (SÓLO del handler síncrono)
// ──────────────────────────────────────────────
// 1. ReadFormAsync — sin buffering.
// 2. Validar X-Twilio-Signature HMAC-SHA1.             → 403 si inválida.
// 3. Parsear TwilioMessageStatusRequest.
// 4. Resolver tenant por ClinicPhone (número origen).  → 200 si no encontrado.
// 5. Idempotencia: (twilio.message_status, SID_status, tenantId).
//    → 200 inmediato si duplicado.
// 6. Persistir WebhookEvent (trazabilidad).
// 7. Llamar a IMessageStatusService.ProcessAsync (actualiza Message + inserta
//    MessageDeliveryEvent). Esta es la única "carga pesada" permitida en el
//    handler porque es una sola escritura DB O(1) y cabe en < 100 ms.
//    Twilio espera respuesta en < 5 s; la escritura DB en LAN es < 10 ms.
// 8. Responder 200 vacío (no TwiML; los callbacks de estado no necesitan XML).
//
// ¿POR QUÉ NO ENCOLAR?
// ─────────────────────
// Los callbacks de estado son O(1) en base de datos:
//   · 1 SELECT en messages por (tenant_id, provider_message_id)
//   · 1 UPDATE en messages
//   · 1 INSERT en message_delivery_events
// Total: ≤ 3 sentencias en LAN < 10 ms. No justifica la complejidad de
// un Channel + BackgroundWorker adicional. Si la carga creciera (miles
// de callbacks/s), se añadiría el worker en ese momento.
//
// SEGURIDAD
// ─────────
// · AllowAnonymous: los callbacks de Twilio no llevan JWT.
// · 403 en firma inválida.
// · 200 en tenant no encontrado (evita reintentos de Twilio para SIDs huérfanos).
//
// IDEMPOTENCIA
// ─────────────
// Clave: ("twilio.message_status", "{MessageSid}_{MessageStatus}", tenantId)
// El mismo callback (mismo SID + mismo status) → 200 sin reprocesar.
// Callbacks distintos del mismo SID (sent, delivered, read) → distintas claves.
// ════════════════════════════════════════════════════════════════════════════

public static class MessageStatusEndpoints
{
    public static IEndpointRouteBuilder MapMessageStatusEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/twilio/message-status", HandleAsync)
           .WithName("TwilioMessageStatusWebhook")
           .WithTags("Webhooks", "Twilio", "MessageStatus")
           .WithSummary("Callback de estado de mensaje de Twilio (delivered/read/failed)")
           .WithDescription(
               "Recibe callbacks de cambio de estado de mensajes outbound. " +
               "Actualiza messages y persiste MessageDeliveryEvent para analytics.")
           .AllowAnonymous()
           .DisableAntiforgery();

        return app;
    }

    // ── Handler ───────────────────────────────────────────────────────────────

    private static async Task<IResult> HandleAsync(
        HttpContext                    ctx,
        ITwilioSignatureValidator      signatureValidator,
        ITenantPhoneResolver           phoneResolver,
        IIdempotencyService            idempotency,
        IMessageStatusService          statusService,
        AppDbContext                   db,
        IOptions<TwilioOptions>        twilioOptions,
        ILoggerFactory                 loggerFactory,
        CancellationToken              ct)
    {
        var logger  = loggerFactory.CreateLogger("ClinicBoost.Webhooks.MessageStatus");
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
                TwilioMessageStatusRequest.ToSignatureParams(form),
                webhookUrl,
                twilioSig))
        {
            logger.LogWarning(
                "[MsgStatusWebhook] Firma inválida rechazada. " +
                "IP={IP} URL={URL}",
                ctx.Connection.RemoteIpAddress, webhookUrl);

            return Results.StatusCode(403);
        }

        // ── 4. Parsear campos del callback ────────────────────────────────────
        var statusReq = TwilioMessageStatusRequest.FromForm(form);

        if (string.IsNullOrWhiteSpace(statusReq.MessageSid) ||
            string.IsNullOrWhiteSpace(statusReq.MessageStatus))
        {
            logger.LogWarning(
                "[MsgStatusWebhook] Callback con campos obligatorios vacíos. " +
                "MessageSid='{Sid}' Status='{Status}'",
                statusReq.MessageSid, statusReq.MessageStatus);
            return Results.Ok();
        }

        // ── 5. Resolver tenant por número origen (From) ───────────────────────
        // En callbacks de estado, From es el número de la clínica (origen del envío).
        // Eliminamos el prefijo "whatsapp:" si lo lleva.
        var clinicPhone = statusReq.From.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? statusReq.From["whatsapp:".Length..]
            : statusReq.From;

        var tenantId = await phoneResolver.ResolveAsync(clinicPhone, ct);

        if (!tenantId.HasValue)
        {
            logger.LogWarning(
                "[MsgStatusWebhook] Número origen no asociado a ningún tenant activo. " +
                "From={From} ClinicPhone={Phone} MessageSid={Sid}",
                statusReq.From, clinicPhone, statusReq.MessageSid);
            return Results.Ok();   // 200 para evitar reintentos de Twilio
        }

        // ── 6. Payload para hash de idempotencia ──────────────────────────────
        var rawPayload = string.Concat(
            form.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}&"));

        // ── 7. Idempotencia — clave = SID + "_" + status ─────────────────────
        var idemResult = await idempotency.TryProcessAsync(
            eventType: "twilio.message_status",
            eventId:   statusReq.IdempotencyEventId,   // "SM…_delivered"
            tenantId:  tenantId,
            payload:   rawPayload,
            ct:        ct);

        if (idemResult.IsError)
        {
            logger.LogError(
                "[MsgStatusWebhook] Error de idempotencia. " +
                "MessageSid={Sid} Status={Status} TenantId={TenantId}",
                statusReq.MessageSid, statusReq.MessageStatus, tenantId);
            return Results.StatusCode(500);
        }

        if (idemResult.IsPayloadMismatch)
        {
            logger.LogCritical(
                "[MsgStatusWebhook] Payload hash mismatch para {EventId} " +
                "TenantId={TenantId}. Posible replay attack.",
                statusReq.IdempotencyEventId, tenantId);
            return Results.Ok();
        }

        if (idemResult.AlreadyProcessed)
        {
            logger.LogDebug(
                "[MsgStatusWebhook] Callback duplicado ignorado. " +
                "EventId={EventId} TenantId={TenantId}",
                statusReq.IdempotencyEventId, tenantId);
            return Results.Ok();
        }

        // ── 8. Persistir WebhookEvent (trazabilidad) ──────────────────────────
        var sigHeader = twilioSig.Length > 8
            ? $"{{\"X-Twilio-Signature\":\"{twilioSig[..8]}…\"}}"
            : $"{{\"X-Twilio-Signature\":\"{twilioSig}\"}}";

        var webhookEvent = new WebhookEvent
        {
            TenantId       = tenantId,
            Source         = "twilio",
            EventType      = "message_status",
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

        // ── 9. Procesar: actualizar Message + insertar MessageDeliveryEvent ────
        // O(1) en BD: SELECT + UPDATE + INSERT ≤ 10 ms en LAN.
        // No se encola porque no justifica la complejidad de un Channel/Worker.
        await statusService.ProcessAsync(tenantId.Value, statusReq, ct);

        logger.LogInformation(
            "[MsgStatusWebhook] Procesado. " +
            "MessageSid={Sid} Status={Status} TenantId={TenantId}",
            statusReq.MessageSid, statusReq.MessageStatus, tenantId);

        // ── 10. Responder 200 vacío ───────────────────────────────────────────
        // Twilio no espera TwiML en callbacks de estado (solo necesita 200).
        return Results.Ok();
    }
}
