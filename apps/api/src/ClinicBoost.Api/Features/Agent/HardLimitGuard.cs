namespace ClinicBoost.Api.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// HardLimitGuard
//
// Verifica los hard limits sobre el AgentResult ANTES de que sea devuelto
// al worker. Si algún límite es violado, bloquea la respuesta y devuelve
// una derivación a humano.
//
// HARD LIMITS VERIFICADOS
// ────────────────────────
//   [HL-1] Confirmación de reserva directa
//          · Si ResponseText contiene patrones de confirmación + no hay
//            acción ProposeAppointment del backend → bloquear.
//
//   [HL-2] Descuento excesivo
//          · Si ResponseText menciona un % superior a DiscountMaxPct → bloquear.
//          · Si DiscountMaxPct == 0 y se menciona cualquier descuento → bloquear.
//
//   [HL-3] Confianza de intención insuficiente
//          · Si Intent.IsLowConfidence y Action != EscalateToHuman → derivar.
//
//   [HL-4] Intenciones que siempre requieren derivación
//          · Intent.Complaint, Intent.EscalateToHuman → derivar siempre.
//
//   [HL-5] Ventana de sesión expirada + texto libre
//          · Si !IsInsideSessionWindow y Action == SendMessage → bloquear.
//
// DISEÑO
// ──────
// · La guardia es el último filtro antes de devolver el resultado.
// · Registrar como Singleton (no tiene estado mutable).
// · Los checks son O(1): regex + comparaciones. No llama a la BD ni a OpenAI.
// · Cuando bloquea, sustituye la respuesta por un mensaje de derivación genérico
//   y marca WasBlocked = true para trazabilidad.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Verifica los hard limits y bloquea respuestas que los incumplan.
/// Registrar como <b>Singleton</b>.
/// </summary>
public sealed class HardLimitGuard
{
    // Palabras que indican confirmación directa de reserva (español + inglés)
    private static readonly string[] BookingConfirmationPatterns =
    [
        "he reservado",
        "te he reservado",
        "la cita está confirmada",
        "cita confirmada",
        "he confirmado",
        "queda confirmada",
        "your appointment is confirmed",
        "i have booked",
        "i've booked",
        "booked successfully",
    ];

    // Palabras que indican oferta de descuento
    private static readonly string[] DiscountPatterns =
    [
        "%",
        "descuento",
        "descuentos",
        "rebaja",
        "promo",
        "promoción",
        "oferta",
        "precio especial",
        "discount",
        "off",
    ];

    private readonly ILogger<HardLimitGuard> _logger;

    public HardLimitGuard(ILogger<HardLimitGuard> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Evalúa el <paramref name="result"/> contra los hard limits del contexto.
    /// Si algún límite es violado, devuelve un resultado bloqueado.
    /// </summary>
    public AgentResult Evaluate(AgentResult result, AgentContext ctx)
    {
        // ── [HL-3] Confianza de intención baja → derivar ──────────────────
        if (result.Intent.IsLowConfidence && result.Action != AgentAction.EscalateToHuman)
        {
            return Block(result, ctx,
                $"[HL-3] Confianza baja ({result.Intent.Confidence:P0}) " +
                $"para intención {result.Intent.Intent}.");
        }

        // ── [HL-4] Intenciones que siempre derivan ─────────────────────────
        if (result.Intent.Intent is Intent.Complaint or Intent.EscalateToHuman
            && result.Action != AgentAction.EscalateToHuman)
        {
            return Block(result, ctx,
                $"[HL-4] Intención {result.Intent.Intent} requiere derivación a humano.");
        }

        // ── [HL-5] Ventana de sesión expirada ─────────────────────────────
        if (!ctx.IsInsideSessionWindow && result.Action == AgentAction.SendMessage)
        {
            return Block(result, ctx,
                "[HL-5] Ventana de sesión WhatsApp expirada. No se puede enviar texto libre.");
        }

        // Las comprobaciones sobre ResponseText solo aplican si hay texto
        if (string.IsNullOrWhiteSpace(result.ResponseText))
            return result;

        var textLower = result.ResponseText.ToLowerInvariant();

        // ── [HL-1] Confirmación directa de reserva ────────────────────────
        if (result.Action != AgentAction.ProposeAppointment &&
            BookingConfirmationPatterns.Any(p =>
                textLower.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return Block(result, ctx,
                "[HL-1] La respuesta contiene una confirmación directa de reserva " +
                "sin usar propose_appointment.");
        }

        // ── [HL-2] Descuento excesivo ─────────────────────────────────────
        if (DiscountPatterns.Any(p =>
                textLower.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            if (ctx.DiscountMaxPct == 0)
            {
                return Block(result, ctx,
                    "[HL-2] La respuesta menciona descuentos pero DiscountMaxPct = 0.");
            }

            // Intentar extraer el porcentaje mencionado
            var exceeds = ExceedsDiscountLimit(textLower, ctx.DiscountMaxPct);
            if (exceeds)
            {
                return Block(result, ctx,
                    $"[HL-2] La respuesta menciona un descuento superior al límite " +
                    $"de {ctx.DiscountMaxPct}%.");
            }
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private AgentResult Block(AgentResult original, AgentContext ctx, string reason)
    {
        _logger.LogWarning(
            "[HardLimitGuard] BLOQUEADO. Razón={Reason} " +
            "TenantId={TenantId} ConvId={ConvId} OriginalAction={Action}",
            reason, ctx.TenantId, ctx.ConversationId, original.Action);

        return original with
        {
            Action          = AgentAction.EscalateToHuman,
            ResponseText    = "Un momento, te voy a poner en contacto con nuestro equipo " +
                              "para que puedan ayudarte mejor. 👋",
            EscalationReason = reason,
            WasBlocked      = true,
            BlockReason     = reason,
            UpdatedAiContextJson = original.UpdatedAiContextJson,
        };
    }

    /// <summary>
    /// Busca porcentajes numéricos en el texto y comprueba si alguno supera el límite.
    /// Ejemplo: "20% de descuento" con límite 15% → true.
    /// </summary>
    private static bool ExceedsDiscountLimit(string textLower, decimal maxPct)
    {
        // Buscar patrones como "20%", "20 %" o "veinte por ciento"
        var matches = System.Text.RegularExpressions.Regex.Matches(
            textLower, @"(\d+(?:[.,]\d+)?)\s*%");

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var raw = m.Groups[1].Value.Replace(',', '.');
            if (decimal.TryParse(raw,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var pct) && pct > maxPct)
            {
                return true;
            }
        }

        return false;
    }
}
