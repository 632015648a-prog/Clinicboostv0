namespace ClinicBoost.Api.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// IConversationalAgent
//
// Contrato del agente conversacional.
//
// RESPONSABILIDADES
// ─────────────────
//   1. Clasificar la intención del mensaje inbound.
//   2. Construir el prompt de sistema adecuado para la intención.
//   3. Ejecutar el ciclo de tool calling con OpenAI (max N rondas).
//   4. Aplicar hard limits ANTES de devolver la respuesta.
//   5. Devolver un AgentResult inmutable al worker.
//
// HARD LIMITS (invariantes del sistema)
// ──────────────────────────────────────
//   · NUNCA confirmar una reserva directamente — solo ProposeAppointment.
//   · NUNCA proponer un descuento superior a AgentContext.DiscountMaxPct.
//   · SIEMPRE derivar a humano cuando Intent.Confidence < umbral.
//   · SIEMPRE derivar a humano ante Intent.Complaint o Intent.EscalateToHuman.
//   · NUNCA responder con texto libre fuera de la ventana de sesión WA (24 h).
//
// REGISTRO
// ────────
// Registrar como Scoped (depende de AppDbContext vía ToolRegistry).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Agente conversacional principal.
/// Orquesta clasificación de intención, tool calling y hard limits.
/// Registrar como <b>Scoped</b>.
/// </summary>
public interface IConversationalAgent
{
    /// <summary>
    /// Procesa un mensaje inbound y devuelve la acción a ejecutar.
    /// </summary>
    /// <param name="context">
    /// Snapshot completo del contexto: paciente, conversación, configuración del tenant.
    /// </param>
    /// <param name="ct">Token de cancelación del host.</param>
    /// <returns>
    /// <see cref="AgentResult"/> con la acción decidida y los metadatos del turno.
    /// El resultado pasa siempre por <see cref="HardLimitGuard"/> antes de ser devuelto.
    /// </returns>
    Task<AgentResult> HandleAsync(AgentContext context, CancellationToken ct = default);
}
