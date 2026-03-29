using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Infrastructure.Idempotency;

// ════════════════════════════════════════════════════════════════════════════
// IdempotencyService
//
// ESTRATEGIA: INSERT … ON CONFLICT DO NOTHING + SELECT del existente
// ──────────────────────────────────────────────────────────────────
// 1. Intentar INSERT del nuevo ProcessedEvent.
// 2. Si el INSERT no inserta ninguna fila (ON CONFLICT DO NOTHING), significa
//    que ya existía una fila con la misma (event_type, event_id, tenant_id).
// 3. Leer el registro existente y comparar el payload_hash.
//    · Hashes iguales (o ambos null) → duplicado legítimo → AlreadyProcessed.
//    · Hashes distintos              → payload alterado    → PayloadMismatch.
//
// POR QUÉ INSERT+SELECT Y NO SELECT+INSERT
// ─────────────────────────────────────────
// El patrón "check-then-act" tiene TOCTOU (Time-Of-Check Time-Of-Use):
// dos instancias concurrentes podrían ambas leer "no existe" y ambas
// intentar insertar, resultando en doble procesamiento.
// El INSERT atómico con ON CONFLICT elimina esta carrera en Postgres.
//
// THREAD SAFETY
// ─────────────
// El servicio es Scoped (una instancia por request HTTP / job execution).
// No hay estado compartido entre instancias → thread-safe.
//
// PAYLOAD HASH
// ────────────
// SHA-256 del payload serializado como string UTF-8.
// Suficiente para detección de replay; no se usa para criptografía.
// Se almacena como hex lowercase (64 chars) sin prefijo.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="IIdempotencyService"/> basada en Postgres.
/// Usa INSERT … ON CONFLICT DO NOTHING para garantizar atomicidad.
/// </summary>
public class IdempotencyService : IIdempotencyService
{
    private readonly AppDbContext                  _db;
    private readonly ITenantContext                _tenant;
    private readonly ILogger<IdempotencyService>   _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false
    };

    public IdempotencyService(
        AppDbContext                 db,
        ITenantContext               tenant,
        ILogger<IdempotencyService>  logger)
    {
        _db     = db;
        _tenant = tenant;
        _logger = logger;
    }

    // ── API principal ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IdempotencyResult> TryProcessAsync(
        string            eventType,
        string            eventId,
        Guid?             tenantId = null,
        string?           payload  = null,
        string?           metadata = null,
        CancellationToken ct       = default)
    {
        ValidateArgs(eventType, eventId);

        // Resolver tenant: parámetro explícito > ITenantContext > null
        var resolvedTenantId = tenantId ?? (_tenant.IsInitialized ? _tenant.TenantId : null);
        var payloadHash      = payload is not null ? ComputeHash(payload) : null;

        try
        {
            return await TryInsertAndCheckAsync(
                eventType, eventId, resolvedTenantId, payloadHash, metadata, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Idempotency] Error al registrar evento. " +
                "EventType={EventType} EventId={EventId} TenantId={TenantId}",
                eventType, eventId, resolvedTenantId);

            return IdempotencyResult.Failure(ex);
        }
    }

    /// <inheritdoc/>
    public Task<IdempotencyResult> TryProcessAsync<TPayload>(
        string            eventType,
        string            eventId,
        TPayload          payload,
        Guid?             tenantId = null,
        string?           metadata = null,
        CancellationToken ct       = default)
    {
        // Serializar el payload antes de delegar a la sobrecarga base
        var serialized = payload is null
            ? null
            : JsonSerializer.Serialize(payload, _jsonOpts);

        return TryProcessAsync(eventType, eventId, tenantId, serialized, metadata, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> IsAlreadyProcessedAsync(
        string            eventType,
        string            eventId,
        Guid?             tenantId = null,
        CancellationToken ct       = default)
    {
        ValidateArgs(eventType, eventId);

        var resolvedTenantId = tenantId ?? (_tenant.IsInitialized ? _tenant.TenantId : null);

        try
        {
            // NOTA: EF InMemory no soporta correctamente la comparación de Guid? nullable
            // con == en LINQ. Usamos ToList() + LINQ to Objects para garantizar semántica
            // correcta tanto en InMemory (tests) como en Postgres (producción).
            // En Postgres real, AnyAsync directo también funcionaría, pero esta
            // implementación es compatible con ambos proveedores.
            var candidates = await _db.ProcessedEvents
                .AsNoTracking()
                .Where(e => e.EventType == eventType && e.EventId == eventId)
                .ToListAsync(ct);

            return candidates.Any(e =>
                (e.TenantId.HasValue && resolvedTenantId.HasValue &&
                 e.TenantId.Value == resolvedTenantId.Value) ||
                (!e.TenantId.HasValue && !resolvedTenantId.HasValue));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Idempotency] Error al consultar evento. " +
                "EventType={EventType} EventId={EventId}",
                eventType, eventId);
            // Conservador: ante error, asumir "no procesado" para no bloquear.
            // El INSERT posterior resolverá la situación.
            return false;
        }
    }

    // ── Núcleo: INSERT ON CONFLICT + lectura del existente ────────────────────

    private async Task<IdempotencyResult> TryInsertAndCheckAsync(
        string            eventType,
        string            eventId,
        Guid?             tenantId,
        string?           payloadHash,
        string?           metadata,
        CancellationToken ct)
    {
        var newEvent = new ProcessedEvent
        {
            EventType   = eventType,
            EventId     = eventId,
            TenantId    = tenantId,
            PayloadHash = payloadHash,
            Metadata    = metadata,
            ProcessedAt = DateTimeOffset.UtcNow,
        };

        // ── Intentar INSERT atómico (virtual para tests con InMemory) ─────
        var (inserted, existingFromInsert) = await AtomicInsertAsync(newEvent, ct);

        if (inserted)
        {
            // ── Primera vez: procesamiento nuevo ──────────────────────────
            _logger.LogDebug(
                "[Idempotency] Evento nuevo registrado. " +
                "EventType={EventType} EventId={EventId} TenantId={TenantId} Id={Id}",
                eventType, eventId, tenantId, newEvent.Id);

            return IdempotencyResult.NewEvent(newEvent.Id, newEvent.ProcessedAt);
        }

        // ── Ya existía: usar el existente devuelto o releer ───────────────
        // NOTA: EF InMemory no soporta correctamente Guid? == Guid? en LINQ to Entities.
        // Usamos .ToList() + LINQ to Objects para compatibilidad con ambos proveedores.
        ProcessedEvent? existing;
        if (existingFromInsert is not null)
        {
            existing = existingFromInsert;
        }
        else
        {
            var candidates = await _db.ProcessedEvents
                .AsNoTracking()
                .Where(e => e.EventType == eventType && e.EventId == eventId)
                .ToListAsync(ct);

            existing = candidates.FirstOrDefault(e =>
                (e.TenantId.HasValue  && tenantId.HasValue  && e.TenantId.Value == tenantId.Value) ||
                (!e.TenantId.HasValue && !tenantId.HasValue));
        }

        if (existing is null)
        {
            // Situación extremadamente rara: el INSERT falló por conflicto pero
            // ya no existe el registro (race condition con un DELETE concurrente,
            // imposible en nuestra RLS donde processed_events no permite DELETE).
            // Tratarlo como nuevo evento de forma segura.
            _logger.LogWarning(
                "[Idempotency] Conflicto en INSERT pero registro no encontrado. " +
                "EventType={EventType} EventId={EventId} TenantId={TenantId}. " +
                "Reintentando como evento nuevo.",
                eventType, eventId, tenantId);

            return IdempotencyResult.NewEvent(newEvent.Id, newEvent.ProcessedAt);
        }

        // ── Comparar payload hash ─────────────────────────────────────────
        // Si el proveedor re-entrega con mismo ID pero cuerpo distinto →
        // posible replay attack o bug del proveedor.
        if (payloadHash is not null &&
            existing.PayloadHash is not null &&
            !string.Equals(payloadHash, existing.PayloadHash, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[Idempotency] ALERTA: Payload hash mismatch para evento duplicado. " +
                "Posible replay attack o bug del proveedor. " +
                "EventType={EventType} EventId={EventId} TenantId={TenantId} " +
                "ExistingHash={ExistingHash} IncomingHash={IncomingHash}",
                eventType, eventId, tenantId,
                existing.PayloadHash[..8] + "…",   // solo los primeros 8 chars por seguridad
                payloadHash[..8] + "…");

            return IdempotencyResult.PayloadMismatch(existing.Id, existing.ProcessedAt);
        }

        _logger.LogDebug(
            "[Idempotency] Evento duplicado detectado (idempotente). " +
            "EventType={EventType} EventId={EventId} TenantId={TenantId} " +
            "FirstProcessedAt={FirstProcessedAt}",
            eventType, eventId, tenantId, existing.ProcessedAt);

        return IdempotencyResult.Duplicate(existing.Id, existing.ProcessedAt);
    }

    // ── INSERT atómico (virtual para inyección en tests) ──────────────────────

    /// <summary>
    /// Inserta la fila si no existe un conflicto en (event_type, event_id, tenant_id).
    /// Devuelve (true, null) si se insertó, (false, existingRow) si ya existía.
    ///
    /// Marcado como <c>protected virtual</c> para permitir que los tests unitarios
    /// sustituyan la implementación SQL cruda (incompatible con EF InMemory) por
    /// una versión basada en EF puro.
    /// En producción usa ON CONFLICT DO NOTHING de Postgres para garantizar atomicidad.
    /// </summary>
    protected virtual async Task<(bool inserted, ProcessedEvent? existing)>
        AtomicInsertAsync(
            ProcessedEvent    evt,
            CancellationToken ct)
    {
        // SQL directo con FormattableString (parametrizado automáticamente por EF Core).
        // ON CONFLICT DO NOTHING silencia duplicados sin lanzar excepción.
        // NULLS NOT DISTINCT en el índice garantiza que NULL == NULL en la comparación.

        var rowsAffected = await _db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO processed_events
                (id, event_type, event_id, tenant_id, payload_hash, processed_at, metadata)
            VALUES
                ({evt.Id}, {evt.EventType}, {evt.EventId}, {evt.TenantId},
                 {evt.PayloadHash}, {evt.ProcessedAt}, {evt.Metadata})
            ON CONFLICT (event_type, event_id, tenant_id) DO NOTHING
            """,
            ct);

        // rowsAffected = 1 → se insertó (evento nuevo)
        // rowsAffected = 0 → conflicto silenciado (ya existía)
        // En el caso de conflicto, devolvemos null para que el caller haga un SELECT.
        return (rowsAffected == 1, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Calcula SHA-256 del texto como hex lowercase (64 chars).
    /// </summary>
    internal static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void ValidateArgs(string eventType, string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException(
                "eventType no puede estar vacío.", nameof(eventType));

        if (string.IsNullOrWhiteSpace(eventId))
            throw new ArgumentException(
                "eventId no puede estar vacío.", nameof(eventId));
    }
}
