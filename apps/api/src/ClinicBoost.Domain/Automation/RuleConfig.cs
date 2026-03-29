using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Automation;

/// <summary>
/// Regla de negocio configurable por tenant y flujo.
/// Permite personalizar la automatización sin tocar código.
/// Una fila por (tenant_id, flow_id, rule_key). UNIQUE garantizado en BD.
///
/// Ejemplos de reglas:
///   flow_03 / reminder_hours_before    → "24"
///   flow_03 / no_show_wait_minutes     → "15"
///   flow_06 / inactive_days_threshold  → "60"
///   flow_02 / gap_min_duration_minutes → "30"
///   global  / discount_max_pct        → "20"   (DiscountGuard)
///   global  / max_daily_outbound_msgs  → "10"
/// </summary>
public sealed class RuleConfig : BaseEntity
{
    public required string FlowId { get; set; }     // flow_00…flow_07 | global
    public required string RuleKey { get; set; }
    public required string RuleValue { get; set; }  // siempre texto; el backend castea
    public required string ValueType { get; set; }  // string | integer | decimal | boolean | json
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Registro de cada ejecución de un flujo automático.
/// Observabilidad de negocio complementaria a los logs de Serilog.
/// </summary>
public sealed class AutomationRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }

    public required string FlowId { get; init; }
    public required string TriggerType { get; init; }   // scheduled | event | manual
    public string? TriggerRef { get; init; }             // nombre del job o ID del evento

    public required string Status { get; set; }          // running | completed | failed | skipped
    public int ItemsProcessed { get; set; } = 0;
    public int ItemsSucceeded { get; set; } = 0;
    public int ItemsFailed { get; set; } = 0;

    public string? ErrorMessage { get; set; }
    public string? ErrorDetail { get; set; }             // JSON
    public Guid? CorrelationId { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Duración calculada. Null hasta que finaliza la ejecución.</summary>
    public int? DurationMs =>
        FinishedAt.HasValue
            ? (int)(FinishedAt.Value - StartedAt).TotalMilliseconds
            : null;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
