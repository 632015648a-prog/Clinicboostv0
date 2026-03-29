# Guía del Servicio de Idempotencia — ClinicBoost API

> **Tabla**: `processed_events(id, event_type, event_id, tenant_id, payload_hash, processed_at, metadata)`  
> **Patrón**: INSERT … ON CONFLICT DO NOTHING + hash SHA-256 del payload  
> **Ciclo de vida**: `Scoped` — una instancia por request HTTP / job scope

---

## 1. ¿Por qué idempotencia?

Los proveedores externos (Twilio, pasarelas de pago, etc.) **garantizan entrega al menos una vez**. Eso significa:

- Un mismo webhook puede llegar 2 o más veces (timeout en respuesta 200, reintentos automáticos).
- Un job interno puede ejecutarse dos veces si el scheduler falla justo después del inicio.
- El mismo callback de estado puede llegar en distinto orden.

Sin idempotencia, podríamos enviar dos SMS de confirmación, cobrar dos veces o duplicar una cita.

---

## 2. Convención de `event_type`

| Origen                        | `event_type`                        |
|-------------------------------|-------------------------------------|
| Twilio voz inbound            | `twilio.voice_inbound`              |
| Twilio WhatsApp inbound       | `twilio.whatsapp_inbound`           |
| Twilio SMS inbound            | `twilio.sms_inbound`                |
| Twilio status callback (msg)  | `twilio.message_status`             |
| Twilio voice status callback  | `twilio.voice_status`               |
| Job de recordatorio de cita   | `internal.appointment_reminder`     |
| Job de facturación            | `internal.billing_run`              |
| Cualquier flow de automatización | `internal.{flow_id}`             |

> **Regla**: `{proveedor}.{subtipo}` en minúsculas, sin espacios.

---

## 3. Árbol de decisión del `IdempotencyResult`

```
TryProcessAsync(...)
        │
        ├─ IsError = true        → ⚠️ Error de BD. Devolver 500. No procesar.
        │                           Twilio reintentará automáticamente.
        │
        ├─ ShouldProcess = true  → ✅ Primer procesamiento. Ejecutar negocio.
        │   (AlreadyProcessed=false, IsError=false)
        │
        ├─ AlreadyProcessed = true
        │       │
        │       ├─ IsPayloadMismatch = false  → ♻️ Duplicado legítimo. Devolver 200 vacío.
        │       │
        │       └─ IsPayloadMismatch = true   → 🚨 Mismo ID, payload distinto.
        │                                        Posible replay attack. Log + 200/409.
```

---

## 4. Inyección del servicio

El servicio se registra en `ServiceCollectionExtensions`:

```csharp
// src/ClinicBoost.Api/Infrastructure/Extensions/ServiceCollectionExtensions.cs
services.AddScoped<IIdempotencyService, IdempotencyService>();
```

En cualquier handler de Minimal API:

```csharp
app.MapPost("/webhooks/twilio/voice", async (
    [FromBody]          TwilioVoiceRequest  req,
    IIdempotencyService                     idempotency,
    ITenantContext                          tenant,
    CancellationToken                       ct) =>
{
    // ...
});
```

---

## 5. Ejemplos por caso de uso

### 5.1 Twilio Voice Inbound

```csharp
app.MapPost("/webhooks/twilio/voice", async (
    HttpContext         ctx,
    IIdempotencyService idempotency,
    ITenantContext      tenant,
    CancellationToken   ct) =>
{
    // Twilio envía el body como application/x-www-form-urlencoded
    var form       = await ctx.Request.ReadFormAsync(ct);
    var callSid    = form["CallSid"].ToString();          // "CA…"
    var rawPayload = string.Join("&",
        form.Select(kv => $"{kv.Key}={kv.Value}"));      // para hash

    // tenantId puede resolverse por el número "To" o vía header de tu gateway
    var tenantId = ResolveTenantFromPhoneNumber(form["To"]);

    var idempotency_result = await idempotency.TryProcessAsync(
        eventType: "twilio.voice_inbound",
        eventId:   callSid,
        tenantId:  tenantId,
        payload:   rawPayload,         // opcional; habilita detección de replay
        ct:        ct);

    if (idempotency_result.IsError)
        return Results.StatusCode(500);   // Twilio reintentará

    if (idempotency_result.ShouldSkip)
        return Results.Ok();              // 200 idempotente

    // ── Lógica de negocio (solo se ejecuta una vez) ──────────────────────
    await HandleInboundCall(form, tenant, ct);

    return Results.Ok();
});
```

### 5.2 Twilio WhatsApp Inbound

```csharp
app.MapPost("/webhooks/twilio/whatsapp", async (
    HttpContext         ctx,
    IIdempotencyService idempotency,
    CancellationToken   ct) =>
{
    var form       = await ctx.Request.ReadFormAsync(ct);
    var messageSid = form["MessageSid"].ToString();        // "SM…"
    var tenantId   = ResolveTenantFromPhoneNumber(form["To"]);

    // Pasamos el objeto tipado → serializado internamente para el hash
    var webhookPayload = new TwilioWhatsAppPayload(form);

    var result = await idempotency.TryProcessAsync(
        eventType: "twilio.whatsapp_inbound",
        eventId:   messageSid,
        payload:   webhookPayload,           // sobrecarga genérica TryProcessAsync<T>
        tenantId:  tenantId,
        ct:        ct);

    if (result.IsError)   return Results.StatusCode(500);
    if (result.ShouldSkip) return Results.Ok();

    await ProcessWhatsAppMessage(form, ct);
    return Results.Ok();
});
```

### 5.3 Twilio Status Callback (entrega de mensajes)

Los status callbacks pueden llegar **fuera de orden** y múltiples veces por mensaje.
La clave de idempotencia es `(twilio.message_status, MessageSid + "_" + MessageStatus)`.

```csharp
app.MapPost("/webhooks/twilio/status", async (
    HttpContext         ctx,
    IIdempotencyService idempotency,
    CancellationToken   ct) =>
{
    var form      = await ctx.Request.ReadFormAsync(ct);
    var sid       = form["MessageSid"].ToString();
    var status    = form["MessageStatus"].ToString();     // "delivered", "failed"…
    var tenantId  = ResolveTenantFromAccountSid(form["AccountSid"]);

    // Componer el eventId incluyendo el status para que cada transición
    // sea un evento único (no idempotente entre estados distintos).
    var eventId   = $"{sid}_{status}";

    var result = await idempotency.TryProcessAsync(
        eventType: "twilio.message_status",
        eventId:   eventId,
        tenantId:  tenantId,
        ct:        ct);

    if (result.IsError)    return Results.StatusCode(500);
    if (result.ShouldSkip) return Results.Ok();

    await UpdateMessageStatus(sid, status, ct);
    return Results.Ok();
});
```

> **Alternativa**: usar solo `MessageSid` como `eventId` si solo te interesa registrar
> el primer estado. La elección depende del modelo de dominio.

### 5.4 Twilio Voice Status Callback

```csharp
app.MapPost("/webhooks/twilio/voice/status", async (
    HttpContext         ctx,
    IIdempotencyService idempotency,
    CancellationToken   ct) =>
{
    var form     = await ctx.Request.ReadFormAsync(ct);
    var callSid  = form["CallSid"].ToString();
    var status   = form["CallStatus"].ToString();    // "completed", "no-answer"…
    var tenantId = ResolveTenantFromAccountSid(form["AccountSid"]);

    var result = await idempotency.TryProcessAsync(
        eventType: "twilio.voice_status",
        eventId:   $"{callSid}_{status}",
        tenantId:  tenantId,
        ct:        ct);

    if (result.IsError)    return Results.StatusCode(500);
    if (result.ShouldSkip) return Results.Ok();

    await UpdateCallRecord(callSid, status, ct);
    return Results.Ok();
});
```

### 5.5 Job interno — Recordatorio de cita

Los jobs internos normalmente tienen un `runId` único generado por el scheduler.
El `tenantId` proviene del `ITenantContext` o se pasa explícitamente.

```csharp
// Ejemplo en un Background Service / Hosted Service
public class AppointmentReminderJob(
    IIdempotencyService idempotency,
    IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(
        Guid   appointmentId,
        string runId,          // UUID único por ejecución del scheduler
        Guid   tenantId,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var idempotencySvc = scope.ServiceProvider
            .GetRequiredService<IIdempotencyService>();

        var result = await idempotencySvc.TryProcessAsync(
            eventType: "internal.appointment_reminder",
            eventId:   runId,                    // identificador único de la ejecución
            tenantId:  tenantId,
            metadata:  $"{{\"appointmentId\":\"{appointmentId}\"}}",
            ct:        ct);

        if (result.IsError)
        {
            _logger.LogError(result.Error,
                "No se pudo registrar idempotencia para reminder {RunId}", runId);
            return;   // Dejar que el scheduler reintente en la siguiente ventana
        }

        if (result.ShouldSkip)
        {
            _logger.LogInformation(
                "Reminder {RunId} ya fue procesado en {At}",
                runId, result.FirstProcessedAt);
            return;
        }

        // ── Enviar recordatorio ──────────────────────────────────────────
        await SendReminderSms(appointmentId, ct);
    }
}
```

### 5.6 Job interno — Verificación previa sin registro

Útil cuando la decisión de **si insertar** la toma otro componente
(por ejemplo, una transacción larga que incluye su propio SaveChangesAsync).

```csharp
// Solo comprueba — no inserta en processed_events
var alreadyDone = await idempotency.IsAlreadyProcessedAsync(
    eventType: "internal.billing_run",
    eventId:   billingRunId,
    tenantId:  tenantId,
    ct:        ct);

if (alreadyDone)
{
    _logger.LogInformation("Billing run {Id} ya procesado, saltando.", billingRunId);
    return;
}

// … aquí TryProcessAsync se llama dentro de una UoW más grande
```

---

## 6. Manejo del `IsPayloadMismatch`

Cuando el mismo `event_id` llega con contenido diferente es una señal de alerta:

```csharp
var result = await idempotency.TryProcessAsync(
    eventType: "twilio.whatsapp_inbound",
    eventId:   messageSid,
    tenantId:  tenantId,
    payload:   rawPayload,
    ct:        ct);

switch (result)
{
    case { IsError: true }:
        return Results.StatusCode(500);

    case { IsPayloadMismatch: true }:
        // El mismo MessageSid llega con body diferente.
        // Devolver 200 (no queremos que Twilio reintente ad infinitum)
        // pero registrar alerta para revisión manual.
        _logger.LogCritical(
            "[SECURITY] Payload mismatch en {EventType}/{EventId}. " +
            "Posible replay attack. TenantId={TenantId}",
            "twilio.whatsapp_inbound", messageSid, tenantId);
        return Results.Ok();   // 200 para frenar los reintentos

    case { ShouldSkip: true }:
        return Results.Ok();   // duplicado legítimo

    default:   // ShouldProcess = true
        await ProcessMessage(rawPayload, ct);
        return Results.Ok();
}
```

---

## 7. Patrón de deconstrucción rápida

Para handlers simples donde no importa el hash:

```csharp
var (shouldProcess, eventRecordId) = await idempotency.TryProcessAsync(
    "twilio.voice_inbound", callSid, tenantId: (Guid?)tenantId);

if (!shouldProcess)
    return Results.Ok();

// … lógica de negocio …
```

---

## 8. Comportamiento multi-tenant

La clave de unicidad en Postgres es `(event_type, event_id, tenant_id)` con `NULLS NOT DISTINCT`:

| TenantId A | TenantId B | ¿Colisión? |
|------------|------------|-----------|
| `guid-1`   | `guid-1`   | ✅ Sí (mismo tenant) |
| `guid-1`   | `guid-2`   | ❌ No (tenants distintos) |
| `null`     | `guid-1`   | ❌ No (global vs. tenant) |
| `null`     | `null`     | ✅ Sí (ambos globales) |

Esto permite que el mismo `MessageSid` de Twilio sea un evento independiente
para cada tenant (caso raro pero posible en configuraciones multi-tenant donde
un número compartido enruta a distintos tenants por horario).

---

## 9. Esquema SQL de referencia

```sql
-- migrations/XXXX_idempotency_service.sql
CREATE TABLE IF NOT EXISTS processed_events (
    id           UUID            NOT NULL DEFAULT gen_random_uuid(),
    event_type   TEXT            NOT NULL,   -- "twilio.voice_inbound"
    event_id     TEXT            NOT NULL,   -- "CA…" / "SM…" / "job-uuid"
    tenant_id    UUID,                       -- NULL = evento global/multi-tenant
    payload_hash TEXT,                       -- SHA-256 hex, NULL si no se pasó payload
    processed_at TIMESTAMPTZ     NOT NULL DEFAULT now(),
    metadata     TEXT,                       -- JSON libre para diagnóstico

    CONSTRAINT pk_processed_events PRIMARY KEY (id)
);

-- Índice único que garantiza atomicidad del INSERT ON CONFLICT.
-- NULLS NOT DISTINCT hace que (type, id, NULL) == (type, id, NULL).
CREATE UNIQUE INDEX IF NOT EXISTS uix_processed_events_key
    ON processed_events (event_type, event_id, tenant_id)
    NULLS NOT DISTINCT;

-- Índice auxiliar para consultas por tenant
CREATE INDEX IF NOT EXISTS ix_processed_events_tenant
    ON processed_events (tenant_id, processed_at DESC)
    WHERE tenant_id IS NOT NULL;

-- RLS: cada tenant solo ve sus propios eventos
ALTER TABLE processed_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY processed_events_tenant_isolation
    ON processed_events
    USING (
        tenant_id IS NULL OR                              -- eventos globales visibles a todos
        tenant_id = current_setting('app.tenant_id', true)::uuid
    );
```

---

## 10. Resumen de firmas del `IIdempotencyService`

```csharp
// ① Caso más común: payload string ya serializado
Task<IdempotencyResult> TryProcessAsync(
    string            eventType,
    string            eventId,
    Guid?             tenantId = null,   // IMPORTANTE: pasar (Guid?)myGuid si es no-nullable
    string?           payload  = null,
    string?           metadata = null,
    CancellationToken ct       = default);

// ② Sobrecarga tipada: serializa el objeto internamente antes del hash
Task<IdempotencyResult> TryProcessAsync<TPayload>(
    string            eventType,
    string            eventId,
    TPayload          payload,
    Guid?             tenantId = null,
    string?           metadata = null,
    CancellationToken ct       = default);

// ③ Solo consulta, no inserta
Task<bool> IsAlreadyProcessedAsync(
    string            eventType,
    string            eventId,
    Guid?             tenantId = null,
    CancellationToken ct       = default);
```

> ⚠️ **Trampa de sobrecarga**: cuando `tenantId` es de tipo `Guid` (no nullable),
> el compilador puede resolver la llamada a `TryProcessAsync<Guid>` (sobrecarga genérica)
> en lugar de a la sobrecarga con `Guid? tenantId`.
> **Siempre usa `tenantId: (Guid?)myGuid`** para forzar la sobrecarga correcta:
> ```csharp
> // ✅ Correcto
> await idempotency.TryProcessAsync("type", "id", tenantId: (Guid?)myTenantId);
>
> // ❌ Ambiguo — el compilador puede elegir la sobrecarga <Guid>
> await idempotency.TryProcessAsync("type", "id", myTenantId);
> ```

---

## 11. Tests disponibles (141 tests verdes)

| Fichero | Grupo | Cobertura |
|---------|-------|-----------|
| `IdempotencyResultTests.cs` | Unitario puro | Todos los estados del `IdempotencyResult` |
| `IdempotencyServiceHashTests.cs` | Unitario | `ComputeHash`: SHA-256, consistencia, null |
| `IdempotencyServiceTests.cs` | Integración ligera (EF InMemory) | 10 grupos: primer procesamiento, duplicados, payload mismatch, aislamiento multi-tenant, TenantContext automático, sobrecarga tipada, `IsAlreadyProcessedAsync`, validación de argumentos, concurrencia simulada, distintos event_types |
