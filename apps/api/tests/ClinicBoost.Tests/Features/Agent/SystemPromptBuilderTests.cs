using ClinicBoost.Api.Features.Agent;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;

namespace ClinicBoost.Tests.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// SystemPromptBuilderTests
//
// Tests unitarios del SystemPromptBuilder.
//
// COBERTURA
// ─────────
//   · Hard limits presentes en todos los prompts
//   · Personalización con nombre de clínica, paciente, idioma
//   · Secciones específicas por intención
//   · Estado de ventana de sesión WhatsApp
//   · Límite de descuento embebido en el prompt
// ════════════════════════════════════════════════════════════════════════════

public sealed class SystemPromptBuilderTests
{
    private static readonly SystemPromptBuilder Builder = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentContext BuildCtx(
        decimal discountMaxPct    = 15m,
        bool    sessionActive     = true,
        string  clinicName        = "FisioTest",
        string  patientName       = "Carlos López",
        string  lang              = "es") => new()
    {
        TenantId              = Guid.NewGuid(),
        PatientId             = Guid.NewGuid(),
        ConversationId        = Guid.NewGuid(),
        CorrelationId         = "corr",
        MessageSid            = "SM001",
        InboundText           = "Quiero una cita",
        PatientName           = patientName,
        PatientPhone          = "+34600000001",
        RgpdConsent           = true,
        ConversationStatus    = "open",
        AiContextJson         = "{}",
        IsInsideSessionWindow = sessionActive,
        RecentMessages        = Array.Empty<Message>(),
        DiscountMaxPct        = discountMaxPct,
        ClinicName            = clinicName,
        LanguageCode          = lang,
    };

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Hard limits presentes en todos los prompts
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(Intent.BookAppointment)]
    [InlineData(Intent.CancelAppointment)]
    [InlineData(Intent.GeneralInquiry)]
    [InlineData(Intent.Complaint)]
    [InlineData(Intent.DiscountRequest)]
    [InlineData(Intent.EscalateToHuman)]
    [InlineData(Intent.Unknown)]
    public void Build_ContainsAllHardLimits_ForEveryIntent(Intent intent)
    {
        var prompt = Builder.Build(BuildCtx(), intent);

        prompt.Should().Contain("HL-1",  because: "HL-1 (no confirmar reserva) debe estar siempre");
        prompt.Should().Contain("HL-2",  because: "HL-2 (límite de descuento) debe estar siempre");
        prompt.Should().Contain("HL-3",  because: "HL-3 (derivar sin contexto) debe estar siempre");
        prompt.Should().Contain("HL-4",  because: "HL-4 (derivar si lo piden) debe estar siempre");
        prompt.Should().Contain("HL-5",  because: "HL-5 (no PII) debe estar siempre");
        prompt.Should().Contain("HL-6",  because: "HL-6 (ventana sesión WA) debe estar siempre");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Personalización con nombre de clínica y paciente
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ContainsClinicName_InPrompt()
    {
        var prompt = Builder.Build(BuildCtx(clinicName: "FisioSalud"), Intent.BookAppointment);
        prompt.Should().Contain("FisioSalud");
    }

    [Fact]
    public void Build_ContainsPatientName_InPrompt()
    {
        var prompt = Builder.Build(BuildCtx(patientName: "Marta Ruiz"), Intent.GeneralInquiry);
        prompt.Should().Contain("Marta Ruiz");
    }

    [Fact]
    public void Build_ContainsLanguage_InPrompt()
    {
        var prompt = Builder.Build(BuildCtx(lang: "ca"), Intent.BookAppointment);
        prompt.Should().Contain("catalán");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Descuento máximo en el prompt
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ContainsDiscountMaxPct_InPrompt()
    {
        var prompt = Builder.Build(BuildCtx(discountMaxPct: 20m), Intent.BookAppointment);
        prompt.Should().Contain("20");
    }

    [Fact]
    public void Build_ContainsNoDiscountMessage_WhenMaxIsZero()
    {
        var prompt = Builder.Build(BuildCtx(discountMaxPct: 0m), Intent.DiscountRequest);
        prompt.Should().Contain("0%");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: Estado de ventana de sesión
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ContainsSessionActiveMessage_WhenInsideWindow()
    {
        var prompt = Builder.Build(BuildCtx(sessionActive: true), Intent.BookAppointment);
        prompt.Should().Contain("activa",
            because: "ventana activa debe indicarse claramente");
    }

    [Fact]
    public void Build_ContainsSessionExpiredWarning_WhenOutsideWindow()
    {
        var prompt = Builder.Build(BuildCtx(sessionActive: false), Intent.BookAppointment);
        prompt.Should().Contain("EXPIRADO",
            because: "ventana expirada debe advertir al agente");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: Secciones específicas por intención
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ContainsBookingSection_ForBookAppointment()
    {
        var prompt = Builder.Build(BuildCtx(), Intent.BookAppointment);
        prompt.Should().Contain("propose_appointment",
            because: "la sección de reserva debe mencionar la tool");
    }

    [Fact]
    public void Build_ContainsCancellationSection_ForCancelAppointment()
    {
        var prompt = Builder.Build(BuildCtx(), Intent.CancelAppointment);
        prompt.Should().Contain("propose_cancellation");
    }

    [Fact]
    public void Build_ContainsEscalateInstruction_ForComplaint()
    {
        var prompt = Builder.Build(BuildCtx(), Intent.Complaint);
        prompt.Should().Contain("escalate_to_human");
        prompt.Should().Contain("SIEMPRE", because: "las quejas siempre se derivan");
    }

    [Fact]
    public void Build_ContainsEscalateInstruction_ForUnknown()
    {
        var prompt = Builder.Build(BuildCtx(), Intent.Unknown);
        prompt.Should().Contain("escalate_to_human");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 6: Formato y longitud
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ReturnsNonEmptyString_ForAllIntents()
    {
        foreach (Intent intent in Enum.GetValues<Intent>())
        {
            var prompt = Builder.Build(BuildCtx(), intent);
            prompt.Should().NotBeNullOrWhiteSpace(
                $"el prompt para {intent} no puede estar vacío");
        }
    }

    [Fact]
    public void Build_IsReasonablyLong_ContainingMinimumContent()
    {
        var prompt = Builder.Build(BuildCtx(), Intent.BookAppointment);
        prompt.Length.Should().BeGreaterThan(500,
            "el prompt debe tener suficiente contenido para guiar al agente");
    }
}
