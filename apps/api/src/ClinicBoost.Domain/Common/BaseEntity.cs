namespace ClinicBoost.Domain.Common;

/// <summary>
/// Entidad base con tenant_id obligatorio.
/// TODAS las entidades de negocio deben heredar de esta clase.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Identificador del tenant (clínica). NUNCA puede ser vacío.
    /// La RLS de Postgres lo usa como filtro automático.
    /// </summary>
    public Guid TenantId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Registro de idempotencia para webhooks y eventos externos.
/// Evita procesar el mismo evento dos veces.
/// </summary>
public sealed class ProcessedEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Tipo de evento: "twilio.message", "supabase.auth", etc.</summary>
    public required string EventType { get; init; }

    /// <summary>ID único del evento según el proveedor externo.</summary>
    public required string EventId { get; init; }

    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Metadata { get; init; }
}

/// <summary>
/// Log de auditoría para cambios críticos en entidades sensibles.
/// Separado del log de aplicación (Serilog).
/// </summary>
public sealed class AuditLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public required string EntityType { get; init; }
    public Guid EntityId { get; init; }
    public required string Action { get; init; }   // "created", "updated", "deleted"
    public string? OldValues { get; init; }         // JSON serializado
    public string? NewValues { get; init; }         // JSON serializado
    public Guid? ActorId { get; init; }             // Usuario que realizó la acción
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
