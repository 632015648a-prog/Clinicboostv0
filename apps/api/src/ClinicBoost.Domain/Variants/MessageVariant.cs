namespace ClinicBoost.Domain.Variants;

// ════════════════════════════════════════════════════════════════════════════
// MessageVariant
//
// Catálogo de variantes A/B por flujo y plantilla de mensaje.
//
// DISEÑO
// ──────
// · Una fila = una variante activa o histórica para un par (flow_id, template_id).
// · VariantKey es corto y legible ("A", "B", "control", "v2_emoji", …).
// · WeightPct controla la distribución aleatoria (0-100). La suma de variantes
//   activas por (tenant, flow, template) debe ser 100.
// · La selección de variante se hace en el orchestrator / sender al enviar
//   un mensaje outbound. El ID resultante se propaga al Message y a los eventos.
//
// REGLA DE NEGOCIO
// ────────────────
// · La lógica de descuento/precio NUNCA vive en la variante.
//   Las variantes solo controlan el cuerpo del mensaje (copy, tono, CTA).
// · El backend valida la cita; la variante solo mide conversión.
//
// TENANT
// ──────
// · tenant_id en todas las filas (ADR-001).
// · RLS activa en Postgres; EF nunca bypass.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Variante A/B de una plantilla de mensaje para un flujo concreto.
/// Registrar como dato mutable (no inmutable): se pueden activar/desactivar.
/// </summary>
public sealed class MessageVariant
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Tenant propietario. Nunca null (ADR-001).</summary>
    public Guid TenantId { get; init; }

    // ── Identificadores de agrupación ─────────────────────────────────────

    /// <summary>
    /// Identificador del flujo al que pertenece la variante (flow_00 … flow_07).
    /// </summary>
    public required string FlowId { get; init; }

    /// <summary>
    /// ID de la plantilla Twilio o clave interna del template.
    /// Ejemplo: "missed_call_recovery_v1", "waitlist_gap_v2".
    /// </summary>
    public required string TemplateId { get; init; }

    /// <summary>
    /// Clave corta y legible de la variante.
    /// Valores típicos: "A", "B", "control", "v2_emoji", "v2_formal".
    /// Única por (TenantId, FlowId, TemplateId).
    /// </summary>
    public required string VariantKey { get; init; }

    // ── Contenido ─────────────────────────────────────────────────────────

    /// <summary>
    /// Primeros 280 caracteres del cuerpo del mensaje.
    /// Sirve para previsualizar la variante en el dashboard sin consultar Twilio.
    /// </summary>
    public string? BodyPreview { get; set; }

    /// <summary>
    /// Variables de plantilla JSON por defecto para esta variante.
    /// El orchestrator puede sobreescribirlas con datos del paciente.
    /// </summary>
    public string? TemplateVars { get; set; }

    // ── Control de activación y peso ──────────────────────────────────────

    /// <summary>
    /// Indica si esta variante está activa para la distribución de mensajes.
    /// Una variante desactivada no recibe nuevos mensajes pero sus eventos
    /// históricos se conservan para análisis.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Porcentaje de distribución (0-100).
    /// La suma de WeightPct de las variantes activas por grupo debe ser 100.
    /// Ejemplo: A=60, B=40 o A=33, B=33, control=34.
    /// </summary>
    public short WeightPct { get; set; } = 50;

    // ── Metadatos opcionales ──────────────────────────────────────────────

    /// <summary>
    /// JSON con metadatos adicionales: tags, notas del editor, versión semántica.
    /// Ejemplo: {"tags":["emoji","casual"],"author":"marketing","version":"2.1"}
    /// </summary>
    public string Metadata { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }  = DateTimeOffset.UtcNow;
}
