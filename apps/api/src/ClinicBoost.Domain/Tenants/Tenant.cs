using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Tenants;

/// <summary>
/// Representa una clínica de fisioterapia suscrita a ClinicBoost.
/// Es la raíz de todo el contexto multi-tenant.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }           // Identificador URL-friendly
    public required string TimeZone { get; set; }       // Ej: "Europe/Madrid"
    public required string WhatsAppNumber { get; set; } // Twilio número asignado
    public TenantPlan Plan { get; set; } = TenantPlan.Starter;
    public bool IsActive { get; set; } = true;

    // RGPD
    public DateTimeOffset? ConsentAcceptedAt { get; set; }
    public string? ConsentVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum TenantPlan
{
    Starter = 1,   // 149 €/mes — 1 terapeuta
    Growth  = 2,   // 299 €/mes — 2-4 terapeutas
    Scale   = 3    // 499 €/mes — operaciones avanzadas
}
