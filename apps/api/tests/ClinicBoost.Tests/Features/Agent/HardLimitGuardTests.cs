using ClinicBoost.Api.Features.Agent;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// HardLimitGuardTests
//
// Tests unitarios de HardLimitGuard.
//
// COBERTURA
// ─────────
//   · [HL-1] Bloqueo de confirmación directa de reserva
//   · [HL-2] Bloqueo de descuento excesivo / descuento cuando máx = 0
//   · [HL-3] Derivación por confianza baja
//   · [HL-4] Derivación siempre para Complaint / EscalateToHuman
//   · [HL-5] Bloqueo de texto libre con ventana de sesión expirada
//   · Paso libre cuando todos los límites se respetan
// ════════════════════════════════════════════════════════════════════════════

public sealed class HardLimitGuardTests
{
    private static readonly HardLimitGuard Guard =
        new(NullLogger<HardLimitGuard>.Instance);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentContext BuildCtx(
        decimal discountMaxPct    = 0m,
        bool    sessionActive     = true) => new()
    {
        TenantId              = Guid.NewGuid(),
        PatientId             = Guid.NewGuid(),
        ConversationId        = Guid.NewGuid(),
        CorrelationId         = "corr-test",
        MessageSid            = "SMtest",
        InboundText           = "Quiero una cita",
        PatientName           = "Ana García",
        PatientPhone          = "+34600000001",
        RgpdConsent           = true,
        ConversationStatus    = "open",
        AiContextJson         = "{}",
        IsInsideSessionWindow = sessionActive,
        RecentMessages        = Array.Empty<Message>(),
        DiscountMaxPct        = discountMaxPct,
        ClinicName            = "ClínicaTest",
        LanguageCode          = "es",
    };

    private static AgentResult BuildResult(
        AgentAction action,
        Intent      intent      = Intent.BookAppointment,
        double      confidence  = 0.9,
        string?     text        = null) => new()
    {
        Action               = action,
        ResponseText         = text ?? "Aquí tienes la respuesta.",
        Intent               = new IntentClassification
        {
            Intent     = intent,
            Confidence = confidence,
            Reasoning  = "test",
        },
        UpdatedAiContextJson = "{}",
        ModelUsed            = "gpt-4o",
        PromptTokens         = 100,
        CompletionTokens     = 50,
    };

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Paso libre (resultado no bloqueado)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Evaluate_PassesThrough_WhenNoLimitsViolated()
    {
        var ctx    = BuildCtx(discountMaxPct: 10m);
        var result = BuildResult(AgentAction.SendMessage, confidence: 0.9,
            text: "Tu cita está pendiente de confirmación.");

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeFalse();
        evaluated.Action.Should().Be(AgentAction.SendMessage);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: [HL-3] Confianza baja → derivar
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Evaluate_Blocks_WhenConfidenceBelowThreshold()
    {
        var ctx    = BuildCtx();
        var result = BuildResult(AgentAction.SendMessage,
            intent: Intent.GeneralInquiry, confidence: 0.5);

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeTrue("[HL-3] confianza < umbral debe bloquear");
        evaluated.Action.Should().Be(AgentAction.EscalateToHuman);
        evaluated.BlockReason.Should().Contain("HL-3");
    }

    [Fact]
    public void Evaluate_PassesThrough_EscalateToHuman_EvenWithLowConfidence()
    {
        // Si ya era EscalateToHuman con confianza baja, no hace falta bloquear
        var ctx    = BuildCtx();
        var result = BuildResult(AgentAction.EscalateToHuman,
            intent: Intent.Unknown, confidence: 0.3);

        var evaluated = Guard.Evaluate(result, ctx);

        // No debe marcar WasBlocked porque ya era escalación
        evaluated.WasBlocked.Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: [HL-4] Complaint y EscalateToHuman siempre derivan
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(Intent.Complaint)]
    [InlineData(Intent.EscalateToHuman)]
    public void Evaluate_Blocks_WhenIntentRequiresAlwaysEscalate(Intent intent)
    {
        var ctx    = BuildCtx();
        var result = BuildResult(AgentAction.SendMessage, intent: intent, confidence: 0.95);

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeTrue($"[HL-4] intent {intent} siempre debe derivar");
        evaluated.Action.Should().Be(AgentAction.EscalateToHuman);
        evaluated.BlockReason.Should().Contain("HL-4");
    }

    [Theory]
    [InlineData(Intent.Complaint)]
    [InlineData(Intent.EscalateToHuman)]
    public void Evaluate_PassesThrough_WhenAlreadyEscalated_ForForcedIntents(Intent intent)
    {
        var ctx    = BuildCtx();
        var result = BuildResult(AgentAction.EscalateToHuman, intent: intent, confidence: 0.9);

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeFalse("ya era escalación, no es bloqueo nuevo");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: [HL-5] Ventana de sesión expirada
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Evaluate_Blocks_WhenSessionExpired_AndActionIsSendMessage()
    {
        var ctx    = BuildCtx(sessionActive: false);
        var result = BuildResult(AgentAction.SendMessage, confidence: 0.9,
            text: "Hola, ¿en qué puedo ayudarte?");

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeTrue("[HL-5] no se puede enviar texto libre con sesión expirada");
        evaluated.BlockReason.Should().Contain("HL-5");
    }

    [Fact]
    public void Evaluate_PassesThrough_WhenSessionExpired_ButActionIsEscalate()
    {
        var ctx    = BuildCtx(sessionActive: false);
        var result = BuildResult(AgentAction.EscalateToHuman, confidence: 0.9);

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeFalse("EscalateToHuman es siempre válido");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: [HL-1] Confirmación directa de reserva
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("He reservado tu cita para mañana.")]
    [InlineData("Te he reservado el jueves a las 10h.")]
    [InlineData("La cita está confirmada para el viernes.")]
    [InlineData("Cita confirmada: 15 de junio a las 11:00.")]
    public void Evaluate_Blocks_WhenResponseContainsDirectBookingConfirmation(string text)
    {
        var ctx    = BuildCtx();
        var result = BuildResult(AgentAction.SendMessage, confidence: 0.9, text: text);

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeTrue(
            $"[HL-1] '{text}' contiene confirmación directa de reserva");
        evaluated.BlockReason.Should().Contain("HL-1");
    }

    [Fact]
    public void Evaluate_PassesThrough_WhenActionIsProposeAppointment_WithConfirmationText()
    {
        // Si la acción ya es ProposeAppointment, el texto de confirmación está permitido
        var ctx    = BuildCtx();
        var result = BuildResult(AgentAction.ProposeAppointment, confidence: 0.9,
            text: "He preparado la propuesta de cita para que la confirmes.");

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeFalse(
            "ProposeAppointment con texto de propuesta es válido");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 6: [HL-2] Descuento excesivo
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Evaluate_Blocks_WhenDiscountMaxIsZero_AndTextMentionsDiscount()
    {
        var ctx    = BuildCtx(discountMaxPct: 0m);
        var result = BuildResult(AgentAction.SendMessage, confidence: 0.9,
            text: "Podemos ofrecerte un descuento del 10% en tu primera visita.");

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeTrue("[HL-2] descuento no permitido (máx = 0)");
        evaluated.BlockReason.Should().Contain("HL-2");
    }

    [Fact]
    public void Evaluate_Blocks_WhenDiscountExceedsMaxPct()
    {
        var ctx    = BuildCtx(discountMaxPct: 10m);
        var result = BuildResult(AgentAction.SendMessage, confidence: 0.9,
            text: "Te ofrecemos un 25% de descuento en la sesión.");

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeTrue("[HL-2] 25% supera el máximo de 10%");
        evaluated.BlockReason.Should().Contain("HL-2");
    }

    [Fact]
    public void Evaluate_PassesThrough_WhenDiscountIsWithinLimit()
    {
        var ctx    = BuildCtx(discountMaxPct: 20m);
        var result = BuildResult(AgentAction.SendMessage, confidence: 0.9,
            text: "Podemos ofrecerte un 15% de descuento.");

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeFalse("15% está dentro del límite de 20%");
    }

    [Fact]
    public void Evaluate_PassesThrough_WhenNoDiscountMentioned()
    {
        var ctx    = BuildCtx(discountMaxPct: 0m);
        var result = BuildResult(AgentAction.SendMessage, confidence: 0.9,
            text: "Tenemos disponibilidad el martes a las 10h. ¿Te viene bien?");

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.WasBlocked.Should().BeFalse("sin mención de descuento, no hay bloqueo");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 7: Propiedades del resultado bloqueado
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Evaluate_BlockedResult_HasEscalationResponse()
    {
        var ctx    = BuildCtx();
        var result = BuildResult(AgentAction.SendMessage,
            intent: Intent.Complaint, confidence: 0.95);

        var evaluated = Guard.Evaluate(result, ctx);

        evaluated.Action.Should().Be(AgentAction.EscalateToHuman);
        evaluated.ResponseText.Should().NotBeNullOrWhiteSpace(
            "el resultado bloqueado debe tener un mensaje de derivación");
        evaluated.EscalationReason.Should().NotBeNullOrWhiteSpace();
        // Preservar metadatos del turno original
        evaluated.ModelUsed.Should().Be("gpt-4o");
        evaluated.PromptTokens.Should().Be(100);
    }
}
