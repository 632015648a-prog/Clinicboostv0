using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Patients;

/// <summary>
/// Registro INMUTABLE de consentimiento RGPD por paciente y tipo de comunicación.
/// Cada fila es un evento de consentimiento (granted o revoked).
/// La revocación se registra con una nueva fila — nunca se actualiza la existente.
/// Cumplimiento: Reglamento (UE) 2016/679 art. 7 y 17.
/// </summary>
public sealed class PatientConsent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public Guid PatientId { get; init; }

    public required string ConsentType { get; init; }
    // whatsapp_marketing | whatsapp_transactional | email_marketing
    // email_transactional | nps_survey | data_processing

    public required string Action { get; init; }        // granted | revoked
    public required string ConsentVersion { get; init; } // v1.0, v2.0, ...
    public required string Channel { get; init; }        // whatsapp | web | in_person | email

    public string? IpAddress { get; init; }             // solo si canal es web
    public string? UserAgent { get; init; }

    /// <summary>
    /// SHA-256 del texto literal del aviso legal. Permite demostrar
    /// exactamente qué texto aceptó el paciente en caso de litigio.
    /// </summary>
    public string? LegalTextHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    // Sin UpdatedAt: inmutable
}
