using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// HttpICalReader
//
// Implementación de IICalReader que descarga el feed .ics con HttpClient
// y lo parsea manualmente (sin dependencias externas de iCal).
//
// DECISIONES DE DISEÑO
// ─────────────────────
//  · Parser propio (sin Ical.Net) para no añadir dependencias pesadas;
//    soporta los campos DTSTART, DTEND, SUMMARY, UID y TRANSP.
//  · Timeout configurable desde ICalOptions.HttpTimeout.
//  · Peticiones condicionales If-None-Match / If-Modified-Since para ahorrar
//    ancho de banda si el servidor lo soporta.
//  · ContentHash SHA-256 permite detectar cambios sin parsear.
//  · MaxEventsPerFeed protege contra feeds gigantes (DoS).
//  · Registro de IHttpClientFactory con nombre "ICalReader" (ver DI).
//
// REGISTRO EN DI
// ──────────────
//  Singleton (sin estado mutable; IHttpClientFactory es thread-safe).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Lee feeds iCal usando <see cref="IHttpClientFactory"/> con timeout configurable.
/// Implementación de <see cref="IICalReader"/>.
/// </summary>
public sealed class HttpICalReader : IICalReader
{
    private const string HttpClientName = "ICalReader";

    private readonly IHttpClientFactory      _httpFactory;
    private readonly ICalOptions             _opts;
    private readonly ILogger<HttpICalReader> _logger;

    public HttpICalReader(
        IHttpClientFactory      httpFactory,
        IOptions<ICalOptions>   opts,
        ILogger<HttpICalReader> logger)
    {
        _httpFactory = httpFactory;
        _opts        = opts.Value;
        _logger      = logger;
    }

    // ── ReadAsync ─────────────────────────────────────────────────────────────

    public async Task<ICalReadResult> ReadAsync(
        string            url,
        string?           etag         = null,
        DateTimeOffset?   lastModified = null,
        CancellationToken ct           = default)
    {
        // Timeout propio (independiente del ct del caller)
        using var timeoutCts  = new CancellationTokenSource(_opts.HttpTimeout);
        using var linkedCts   = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Petición condicional para ahorrar ancho de banda
            if (!string.IsNullOrEmpty(etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            if (lastModified.HasValue)
                request.Headers.IfModifiedSince = lastModified.Value;

            _logger.LogDebug("[ICalReader] GET {Url} (ETag={ETag})", url, etag ?? "none");

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            // ── 304 Not Modified ──────────────────────────────────────────────
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _logger.LogDebug("[ICalReader] 304 Not Modified. Url={Url}", url);
                return ICalReadResult.NotModified();
            }

            if (!response.IsSuccessStatusCode)
            {
                var msg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.LogWarning("[ICalReader] Error HTTP. Url={Url} Status={Status}", url, msg);
                return ICalReadResult.Failure(msg);
            }

            // ── Leer contenido ────────────────────────────────────────────────
            var raw = await response.Content.ReadAsByteArrayAsync(linkedCts.Token);

            // Hash del contenido crudo para detectar cambios sin parsear
            var hash = Convert.ToHexString(SHA256.HashData(raw));

            // Cabeceras de caché del servidor
            var responseEtag = response.Headers.ETag?.Tag;
            var responseLastModified = response.Content.Headers.LastModified;

            // ── Parsear ───────────────────────────────────────────────────────
            var text  = Encoding.UTF8.GetString(raw);
            var slots = ParseIcs(text, url);

            _logger.LogInformation(
                "[ICalReader] Parseados {Count} slots. Url={Url} Hash={Hash}",
                slots.Count, url, hash[..8]);

            return ICalReadResult.Success(
                slots,
                etag:         responseEtag,
                lastModified: responseLastModified,
                contentHash:  hash);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var msg = $"Timeout ({_opts.HttpTimeout.TotalSeconds}s) al leer {url}";
            _logger.LogWarning("[ICalReader] {Message}", msg);
            return ICalReadResult.Failure(msg);
        }
        catch (OperationCanceledException)
        {
            // Cancelación del caller (ct)
            _logger.LogInformation("[ICalReader] Cancelado por el caller. Url={Url}", url);
            return ICalReadResult.Failure("Operación cancelada.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ICalReader] Error inesperado. Url={Url}", url);
            return ICalReadResult.Failure(ex.Message);
        }
    }

    // ── Parser iCal RFC 5545 (sin dependencias externas) ────────────────────

    private List<ICalSlot> ParseIcs(string text, string sourceUrl)
    {
        var slots    = new List<ICalSlot>(_opts.MaxEventsPerFeed);
        var lines    = UnfoldLines(text);

        string? uid        = null;
        string? summary    = null;
        bool    isOpaque   = true;
        DateTimeOffset? dtStart = null;
        DateTimeOffset? dtEnd   = null;
        bool    inVEvent   = false;

        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inVEvent = true;
                uid = summary = null;
                dtStart = dtEnd = null;
                isOpaque = true;
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inVEvent = false;

                if (dtStart.HasValue && dtEnd.HasValue)
                {
                    slots.Add(new ICalSlot
                    {
                        StartsAtUtc = dtStart.Value.ToUniversalTime(),
                        EndsAtUtc   = dtEnd.Value.ToUniversalTime(),
                        Summary     = summary ?? string.Empty,
                        Uid         = uid     ?? string.Empty,
                        IsOpaque    = isOpaque,
                    });
                }

                if (slots.Count >= _opts.MaxEventsPerFeed)
                {
                    _logger.LogWarning(
                        "[ICalReader] MaxEventsPerFeed ({Max}) alcanzado. Url={Url}",
                        _opts.MaxEventsPerFeed, sourceUrl);
                    break;
                }

                continue;
            }

            if (!inVEvent) continue;

            // Separar nombre de propiedad y valor (puede haber parámetros: DTSTART;TZID=...)
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;

            var propFull = line[..colonIdx];
            var value    = line[(colonIdx + 1)..];

            // Extraer nombre base (sin parámetros)
            var propName = propFull.Contains(';', StringComparison.Ordinal)
                ? propFull[..propFull.IndexOf(';')]
                : propFull;

            // Extraer TZID si existe
            string? tzidParam = null;
            if (propFull.Contains("TZID=", StringComparison.OrdinalIgnoreCase))
            {
                var semiIdx = propFull.IndexOf(';');
                var tzidPart = propFull[(semiIdx + 1)..];
                if (tzidPart.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
                    tzidParam = tzidPart[5..];
            }

            switch (propName.ToUpperInvariant())
            {
                case "UID":
                    uid = value.Trim();
                    break;

                case "SUMMARY":
                    summary = UnescapeText(value);
                    break;

                case "TRANSP":
                    isOpaque = value.Trim().Equals("OPAQUE", StringComparison.OrdinalIgnoreCase);
                    break;

                case "DTSTART":
                    dtStart = ParseIcsDateTime(value, tzidParam);
                    break;

                case "DTEND":
                    dtEnd = ParseIcsDateTime(value, tzidParam);
                    break;

                case "DURATION":
                    // Soporte parcial: si hay DURATION sin DTEND
                    if (dtStart.HasValue && dtEnd is null)
                        dtEnd = dtStart.Value.Add(ParseDuration(value));
                    break;
            }
        }

        slots.Sort((a, b) => a.StartsAtUtc.CompareTo(b.StartsAtUtc));
        return slots;
    }

    // ── Utilidades de parsing ──────────────────────────────────────────────

    /// <summary>
    /// Unfold: las líneas iCal que empiezan con espacio o tab son continuación de la anterior.
    /// </summary>
    private static IEnumerable<string> UnfoldLines(string text)
    {
        var sb    = new StringBuilder();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && sb.Length > 0)
            {
                sb.Append(line[1..]);
            }
            else
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                sb.Append(line);
            }
        }

        if (sb.Length > 0) yield return sb.ToString();
    }

    private static DateTimeOffset? ParseIcsDateTime(string value, string? tzidParam)
    {
        value = value.Trim();

        // Formato UTC: 20240101T120000Z
        if (value.EndsWith('Z') && value.Length >= 15)
        {
            if (TryParseBasic(value[..15], out var dt))
                return new DateTimeOffset(dt, TimeSpan.Zero);
        }

        // Formato local con TZID
        if (tzidParam is not null && value.Length >= 15)
        {
            if (TryParseBasic(value[..15], out var dt))
            {
                try
                {
                    var tz     = TimeZoneConverter.TZConvert.GetTimeZoneInfo(tzidParam);
                    var offset = tz.GetUtcOffset(dt);
                    return new DateTimeOffset(dt, offset);
                }
                catch
                {
                    // TZID desconocida: tratamos como UTC
                    return new DateTimeOffset(dt, TimeSpan.Zero);
                }
            }
        }

        // Formato solo fecha (evento de día entero): 20240101
        if (value.Length == 8 && long.TryParse(value, out _))
        {
            if (TryParseBasic(value + "T000000", out var dt))
                return new DateTimeOffset(dt, TimeSpan.Zero);
        }

        // Fallback: DateTimeOffset.TryParse estándar
        if (DateTimeOffset.TryParse(value, out var result))
            return result;

        return null;
    }

    private static bool TryParseBasic(string basic, out DateTime result)
    {
        // básico: yyyyMMddTHHmmss[Z]
        result = default;
        var s = basic.TrimEnd('Z');
        if (s.Length != 15) return false;
        return DateTime.TryParseExact(
            s, "yyyyMMdd'T'HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out result);
    }

    private static TimeSpan ParseDuration(string value)
    {
        // ISO 8601 simplificado: P1D, PT30M, P1DT2H, P1W, etc.
        value = value.Trim().TrimStart('P');
        var weeks = 0; var days = 0; var hours = 0; var minutes = 0; var seconds = 0;
        var num = new StringBuilder();

        foreach (var c in value)
        {
            if (c == 'T') { num.Clear(); continue; }
            if (char.IsDigit(c)) { num.Append(c); continue; }

            if (!int.TryParse(num.ToString(), out var n)) { num.Clear(); continue; }
            num.Clear();

            switch (c)
            {
                case 'W': weeks   = n; break;
                case 'D': days    = n; break;
                case 'H': hours   = n; break;
                case 'M': minutes = n; break;
                case 'S': seconds = n; break;
            }
        }

        return new TimeSpan((weeks * 7) + days, hours, minutes, seconds);
    }

    private static string UnescapeText(string value)
        => value
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\N", "\n", StringComparison.Ordinal)
            .Replace("\\,", ",",  StringComparison.Ordinal)
            .Replace("\\;", ";",  StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
}
