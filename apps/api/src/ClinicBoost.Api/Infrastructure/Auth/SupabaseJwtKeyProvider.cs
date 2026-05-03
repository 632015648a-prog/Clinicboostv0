using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ClinicBoost.Api.Infrastructure.Auth;

/// <summary>
/// Resuelve claves de verificación de JWTs emitidos por Supabase/GoTrue.
/// Las versiones recientes pueden firmar con ES256 (JWKS) además de HS256 (secreto compartido).
/// </summary>
public sealed class SupabaseJwtKeyProvider
{
    public const string HttpClientName = "SupabaseJwks";

    private const string        JwksCacheKey = "clinicboost:supabase_jwks";
    private static readonly TimeSpan JwksTtl  = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration   _config;
    private readonly IMemoryCache     _cache;

    public SupabaseJwtKeyProvider(
        IHttpClientFactory httpFactory,
        IConfiguration   config,
        IMemoryCache     cache)
    {
        _httpFactory = httpFactory;
        _config      = config;
        _cache       = cache;
    }

    public IEnumerable<SecurityKey> IssuerSigningKeyResolver(
        string                 token,
        SecurityToken        securityToken,
        string?              kid,
        TokenValidationParameters validationParameters)
    {
        var readHandler = new JwtSecurityTokenHandler();
        if (!readHandler.CanReadToken(token)) yield break;

        // Solo header (sin validar aún el firmante).
        var jwt = readHandler.ReadJwtToken(token);
        var alg = jwt.Header.Alg;
        if (string.IsNullOrEmpty(alg) || string.Equals(alg, "HS256", StringComparison.Ordinal))
        {
            var secret = _config["Supabase:JwtSecret"]
                         ?? throw new InvalidOperationException(
                             "Supabase:JwtSecret no configurado; necesario para JWT HS256.");
            yield return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            yield break;
        }

        foreach (var k in GetJwksSigningKeys()) yield return k;
    }

    private IReadOnlyList<SecurityKey> GetJwksSigningKeys()
    {
        if (_cache.TryGetValue(JwksCacheKey, out IReadOnlyList<SecurityKey>? cached) && cached is not null)
            return cached;

        var jwksUrl = BuildJwksUrl();
        var client  = _httpFactory.CreateClient(HttpClientName);
        // Resolver síncrono: evitar I/O en async middleware es incómodo; una petición a JWKS por hora acepta bloqueo breve.
        var json   = client.GetStringAsync(jwksUrl).GetAwaiter().GetResult();
        var jwks   = new JsonWebKeySet(json);
        var list   = jwks.GetSigningKeys() as IReadOnlyList<SecurityKey>
                    ?? jwks.GetSigningKeys().ToList();

        _cache.Set(JwksCacheKey, list, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = JwksTtl,
        });
        return list;
    }

    private string BuildJwksUrl()
    {
        var explicitUrl = _config["Supabase:JwksUrl"];
        if (!string.IsNullOrWhiteSpace(explicitUrl)) return explicitUrl!.Trim();

        var baseUrl = _config["Supabase:Url"]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException(
                "Indica Supabase:Url o Supabase:JwksUrl para validar firmas ES256/RS256.");

        return $"{baseUrl}/auth/v1/.well-known/jwks.json";
    }
}

/// <summary>
/// Aplica <see cref="SupabaseJwtKeyProvider"/> al esquema Bearer (HS256 + JWKS).
/// </summary>
internal sealed class SupabaseJwtBearerPostConfigure(SupabaseJwtKeyProvider provider) : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;
        options.TokenValidationParameters.IssuerSigningKey         = null;
        options.TokenValidationParameters.IssuerSigningKeyResolver = provider.IssuerSigningKeyResolver;
    }
}
