using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ClinicBoost.Api.Infrastructure.Twilio;

/// <summary>
/// Twilio envía <c>application/x-www-form-urlencoded</c>; <c>webhook_events.payload</c> es <c>jsonb</c>.
/// Esta serialización conserva los campos como objeto JSON (clave → valor).
/// </summary>
public static class TwilioFormPayloadJson
{
    public static string FromForm(IFormCollection form)
    {
        var dict = form.OrderBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal);

        return JsonSerializer.Serialize(dict);
    }
}
