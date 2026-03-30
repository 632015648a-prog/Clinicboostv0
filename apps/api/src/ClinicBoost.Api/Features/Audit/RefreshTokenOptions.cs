namespace ClinicBoost.Api.Features.Audit;

public sealed class RefreshTokenOptions
{
    public const string SectionName = "RefreshToken";

    /// <summary>Duración del token en días. Por defecto: 30.</summary>
    public int ExpiryDays { get; set; } = 30;
}
