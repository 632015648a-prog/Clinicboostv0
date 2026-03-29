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
///
/// DISEÑO DE SEGURIDAD:
/// · La tabla NO tiene RLS ni tenant_id obligatorio para que sea accesible
///   desde contextos sin tenant (webhooks públicos de Twilio, jobs internos).
/// · tenant_id es nullable: presente en eventos de tenant conocido,
///   null en webhooks recibidos antes de asociarlos a un tenant.
/// · La unicidad se garantiza en BD con UNIQUE(event_type, event_id, tenant_id),
///   donde tenant_id = NULL se trata como valor distinto en la comparación.
///   Para colisiones cross-tenant se usa event_type+event_id como clave global.
/// · payload_hash (SHA-256 del payload serializado) detecta re-envíos con
///   el mismo ID pero cuerpo diferente (posibles ataques de replay alterado).
///
/// INMUTABLE: una vez insertado nunca se modifica (INSERT only).
/// </summary>
public sealed class ProcessedEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Tipo de evento que categoriza el origen.
    /// Convención: "{proveedor}.{subtipo}" — todo en minúsculas con puntos.
    /// Ejemplos: "twilio.whatsapp_inbound", "twilio.voice_inbound",
    ///           "twilio.message_status", "internal.appointment_reminder".
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>
    /// Identificador único del evento según el proveedor externo.
    /// Ejemplos: Twilio SID "SMxxx", job ID "job-uuid", webhook delivery ID.
    /// Junto con EventType forma la clave de unicidad lógica.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Tenant al que pertenece el evento.
    /// Null cuando el evento se recibe antes de resolverse el tenant
    /// (p.ej. un webhook entrante de Twilio aún sin mapear a clínica).
    /// </summary>
    public Guid? TenantId { get; init; }

    /// <summary>
    /// Hash SHA-256 (hex) del payload serializado del evento.
    /// Detecta re-entregas con mismo ID pero cuerpo alterado (replay attack).
    /// Null cuando el caller no proporciona payload (jobs sin cuerpo).
    /// </summary>
    public string? PayloadHash { get; init; }

    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Metadatos adicionales en formato JSON (opcional).
    /// Usar para almacenar info de diagnóstico: IP de origen, correlation ID, etc.
    /// </summary>
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
