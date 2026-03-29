using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Webhooks;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;

// ════════════════════════════════════════════════════════════════════════════
// MissedCallEndpoints  —  POST /webhooks/twilio/voice
//
// RESPONSABILIDADES DE ESTE ENDPOINT (SÓLO)
// ──────────────────────────────────────────
// 1. Leer el body sin buffering (ReadFormAsync).
// 2. Validar la firma X-Twilio-Signature (rechazar con 403 si inválida).
// 3. Parsear los campos mínimos necesarios.
// 4. Registrar idempotencia (si ya existe → 200 inmediato).
// 5. Persistir WebhookEvent (trazabilidad, cuerpo crudo).
// 6. Encolar el MissedCallJob en el Channel (fire-and-forget).
// 7. Responder 200 con TwiML vacío en < 5 s (exigido por Twilio).
//
// LO QUE NO HACE ESTE ENDPOINT
// ─────────────────────────────
// · NO contacta con la BD más allá de idempotencia + WebhookEvent.
// · NO envía mensajes de WhatsApp.
// · NO crea pacientes.
// · NO ejecuta lógica de negocio de flow_00.
// Todo lo pesado se delega al MissedCallWorker.
//
// SEGURIDAD
// ─────────
// · AllowAnonymous: los webhooks de Twilio no llevan JWT. La autenticación
//   es la validación de firma HMAC-SHA1.
// · Si la validación de firma falla → 403. NO devolver 200 en este caso
//   porque un atacante aprendería que el endpoint existe.
// · Si el tenant no se puede resolver → 200 con TwiML vacío y log de alerta.
//   (Devolver 200 evita reintentos de Twilio para llamadas de números no
//   registrados, p.ej. llamadas spam a nuestro número de prueba.)
//
// IDEMPOTENCIA
// ─────────────
// · Clave: ("twilio.voice_inbound", CallSid, tenantId)
// · Si Twilio re-entrega el mismo CallSid → ya existe en processed_events
//   → se devuelve 200 inmediatamente sin re-procesar.
// · payload hash del body completo detecta re-entregas alteradas.
// ════════════════════════════════════════════════════════════════════════════

public static class MissedCallEndpoints
{
    public static IEndpointRouteBuilder MapMissedCallEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/twilio/voice", HandleAsync)
           .WithName("TwilioVoiceWebhook")
           .WithTags("Webhooks", "Twilio", "Voice")
           .WithSummary("Webhook de voz de Twilio — detección de llamada perdida (flow_00)")
           .WithDescription(
               "Recibe notificaciones de llamadas de Twilio. " +
               "Valida la firma HMAC-SHA1, registra idempotencia y encola el job de flow_00.")
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
        IMissedCallJobQueue            jobQueue,
        AppDbContext                   db,
        IOptions<TwilioOptions>        twilioOptions,
        ILoggerFactory                 loggerFactory,
        CancellationToken              ct)
    {
        var logger  = loggerFactory.CreateLogger("ClinicBoost.Webhooks.Voice");
        var request = ctx.Request;

        // ── 1. Leer body como form ────────────────────────────────────────────
        // ReadFormAsync une body y query params. Twilio envía todo en el body.
        var form = await request.ReadFormAsync(ct);

        // ── 2. Construir la URL de validación ─────────────────────────────────
        // Usa WebhookBaseUrl de config si está disponible (evita problemas de
        // proxies que cambian el scheme o el host). Si no, construye dinámicamente.
        var opts = twilioOptions.Value;
        var baseUrl = string.IsNullOrWhiteSpace(opts.WebhookBaseUrl)
            ? $"{request.Scheme}://{request.Host}"
            : opts.WebhookBaseUrl.TrimEnd('/');
        var webhookUrl = $"{baseUrl}{request.Path}{request.QueryString}";

        // ── 3. Validar firma X-Twilio-Signature ───────────────────────────────
        var twilioSig = request.Headers["X-Twilio-Signature"].ToString();

        if (!signatureValidator.IsValid(
                form.SelectMany(f => f.Value.Select(v =>
                    new KeyValuePair<string, string>(f.Key, v ?? string.Empty))),
                webhookUrl,
                twilioSig))
        {
            logger.LogWarning(
                "[VoiceWebhook] Firma inválida rechazada. " +
                "IP={IP} UserAgent={UA} URL={URL}",
                ctx.Connection.RemoteIpAddress,
                request.Headers.UserAgent.ToString(),
                webhookUrl);

            // 403 para solicitudes con firma inválida (no 200: no queremos
            // que un atacante sepa que el endpoint existe y funciona).
            return Results.StatusCode(403);
        }

        // ── 4. Parsear campos del webhook ─────────────────────────────────────
        var voiceReq = TwilioVoiceRequest.FromForm(form);

        if (string.IsNullOrWhiteSpace(voiceReq.CallSid) ||
            string.IsNullOrWhiteSpace(voiceReq.To))
        {
            logger.LogWarning(
                "[VoiceWebhook] Webhook con campos obligatorios vacíos. Form={Form}",
                string.Join(", ", form.Select(f => $"{f.Key}={f.Value}")));
            return TwimlOk(); // No reintentos de Twilio; el webhook es malformado.
        }

        // ── 5. Resolver tenant por número de la clínica ───────────────────────
        var tenantId = await phoneResolver.ResolveAsync(voiceReq.To, ct);

        if (!tenantId.HasValue)
        {
            logger.LogWarning(
                "[VoiceWebhook] Número no asociado a ningún tenant activo. " +
                "To={To} CallSid={CallSid}",
                voiceReq.To, voiceReq.CallSid);
            // 200 + TwiML vacío: número no registrado, no queremos reintentos.
            return TwimlOk();
        }

        // ── 6. Construir payload serializado para hash de idempotencia ─────────
        // Usamos el form completo como string para el hash; es determinista
        // para la misma entrega de Twilio.
        var rawPayload = string.Concat(
            form.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}&"));

        // ── 7. Idempotencia ───────────────────────────────────────────────────
        var idemResult = await idempotency.TryProcessAsync(
            eventType: "twilio.voice_inbound",
            eventId:   voiceReq.CallSid,
            tenantId:  tenantId,
            payload:   rawPayload,
            ct:        ct);

        if (idemResult.IsError)
        {
            logger.LogError(
                "[VoiceWebhook] Error de idempotencia. " +
                "CallSid={CallSid} TenantId={TenantId}. " +
                "Devolviendo 500 para que Twilio reintente.",
                voiceReq.CallSid, tenantId);
            return Results.StatusCode(500);
        }

        if (idemResult.IsPayloadMismatch)
        {
            logger.LogCritical(
                "[VoiceWebhook] ALERTA: Payload hash mismatch para CallSid={CallSid} " +
                "TenantId={TenantId}. Posible replay attack. Ignorando.",
                voiceReq.CallSid, tenantId);
            return TwimlOk(); // 200 para frenar los reintentos
        }

        if (idemResult.AlreadyProcessed)
        {
            logger.LogDebug(
                "[VoiceWebhook] Evento duplicado ignorado. " +
                "CallSid={CallSid} TenantId={TenantId} " +
                "FirstProcessedAt={FirstProcessedAt}",
                voiceReq.CallSid, tenantId, idemResult.FirstProcessedAt);
            return TwimlOk(); // 200 idempotente
        }

        // ── 8. Persistir WebhookEvent (trazabilidad, sin RLS — tabla pública) ─
        var webhookEvent = new WebhookEvent
        {
            TenantId       = tenantId,
            Source         = "twilio",
            EventType      = "voice_inbound",
            Payload        = rawPayload,
            Headers        = $"{{\"X-Twilio-Signature\":\"{twilioSig[..Math.Min(8, twilioSig.Length)]}…\"}}",
            Status         = "pending",
            IdempotencyKey = idemResult.ProcessedEventId?.ToString(),
            CorrelationId  = Guid.TryParse(ctx.TraceIdentifier, out var traceGuid)
                                 ? traceGuid
                                 : Guid.NewGuid()
        };
        db.WebhookEvents.Add(webhookEvent);
        await db.SaveChangesAsync(ct);

        // ── 9. Encolar job (fire-and-forget) ──────────────────────────────────
        var job = new MissedCallJob(
            TenantId:        tenantId.Value,
            CallSid:         voiceReq.CallSid,
            CallerPhone:     voiceReq.From,
            ClinicPhone:     voiceReq.To,
            CallStatus:      voiceReq.CallStatus,
            ReceivedAt:      DateTimeOffset.UtcNow,
            ProcessedEventId: idemResult.ProcessedEventId!.Value,
            CorrelationId:   ctx.TraceIdentifier);

        var enqueued = await jobQueue.EnqueueAsync(job, ct);

        if (!enqueued)
        {
            logger.LogWarning(
                "[VoiceWebhook] Job no encolado (cola llena). " +
                "CallSid={CallSid} TenantId={TenantId}. " +
                "El webhook ya está en processed_events; Twilio NO reintentará.",
                voiceReq.CallSid, tenantId);
            // Aun así devolvemos 200: el evento ya fue registrado en idempotencia.
            // Si quisiéramos que Twilio reintente, devolveríamos 500 aquí.
        }

        logger.LogInformation(
            "[VoiceWebhook] Procesado exitosamente. " +
            "CallSid={CallSid} TenantId={TenantId} Status={Status} Enqueued={Enqueued}",
            voiceReq.CallSid, tenantId, voiceReq.CallStatus, enqueued);

        // ── 10. Responder con TwiML vacío ─────────────────────────────────────
        return TwimlOk();
    }

    // ── TwiML helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Respuesta TwiML vacía que indica a Twilio "recibido, no hacer nada".
    /// Twilio espera Content-Type: text/xml y cuelga la llamada si no hay verbo.
    /// </summary>
    private static IResult TwimlOk() =>
        Results.Content(
            content:     "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response></Response>",
            contentType: "text/xml; charset=utf-8");
}
