using System.Text.Json;
using ClinicBoost.Api.Features.Conversations;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// ConversationInboxServiceNoteTests  —  WQ-002
//
// Verifica la persistencia de notas en cambio de estado de conversación:
//
//   TC-NOTE-01  PATCH con nota → audit_log con note en new_values.
//   TC-NOTE-02  PATCH sin nota → audit_log con note = null.
//   TC-NOTE-03  PATCH con nota vacía "" → normaliza a null.
//   TC-NOTE-04  Tres cambios sucesivos → statusHistory devuelve 3 desc.
//   TC-NOTE-05  Aislamiento multi-tenant en audit_logs.
//   TC-NOTE-06  ActorId corresponde al UserId del tenant context.
//   TC-NOTE-07  Conversación no encontrada → no se crea audit_log.
// ════════════════════════════════════════════════════════════════════════════

public sealed class ConversationInboxServiceNoteTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid         _tenantId  = Guid.NewGuid();
    private readonly Guid         _patientId = Guid.NewGuid();
    private readonly Guid         _userId    = Guid.NewGuid();

    public ConversationInboxServiceNoteTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"note_tests_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(opts);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedTenantAsync()
    {
        _db.Tenants.Add(new Tenant
        {
            Id             = _tenantId,
            Name           = "Clínica Note Test",
            WhatsAppNumber = "+34600000001",
            Slug           = "clinica-note-test",
            TimeZone       = "Europe/Madrid",
        });
        _db.Patients.Add(new Patient
        {
            Id          = _patientId,
            TenantId    = _tenantId,
            FullName    = "Paciente Test",
            Phone       = "+34666000001",
            Status      = PatientStatus.Active,
            RgpdConsent = true,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<Conversation> SeedConversationAsync(string status = "open")
    {
        var conv = new Conversation
        {
            TenantId  = _tenantId,
            PatientId = _patientId,
            Channel   = "whatsapp",
            FlowId    = "flow_00",
            Status    = status,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();
        return conv;
    }

    private ConversationInboxService BuildService(Guid? actorUserId = null)
    {
        var tenantCtx = Substitute.For<ITenantContext>();
        tenantCtx.UserId.Returns(actorUserId ?? _userId);

        return new ConversationInboxService(
            _db,
            Substitute.For<IOutboundMessageSender>(),
            tenantCtx,
            NullLogger<ConversationInboxService>.Instance);
    }

    // TC-NOTE-01: PATCH con nota → audit_log con note en new_values
    [Fact]
    public async Task PatchStatus_WithNote_PersistsNoteInAuditLog()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("open");
        var service = BuildService();

        var result = await service.PatchStatusAsync(
            _tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "waiting_human", Note = "Paciente enfadado" });

        result.Should().NotBeNull();
        result!.Note.Should().Be("Paciente enfadado");

        var audit = await _db.AuditLogs
            .Where(a => a.EntityType == "conversation" && a.EntityId == conv.Id)
            .FirstOrDefaultAsync();

        audit.Should().NotBeNull();
        audit!.Action.Should().Be("status_changed");

        using var newDoc = JsonDocument.Parse(audit.NewValues!);
        newDoc.RootElement.GetProperty("status").GetString().Should().Be("waiting_human");
        newDoc.RootElement.GetProperty("note").GetString().Should().Be("Paciente enfadado");

        using var oldDoc = JsonDocument.Parse(audit.OldValues!);
        oldDoc.RootElement.GetProperty("status").GetString().Should().Be("open");
    }

    // TC-NOTE-02: PATCH sin nota → audit_log con note = null
    [Fact]
    public async Task PatchStatus_WithoutNote_PersistsNullNote()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("open");
        var service = BuildService();

        var result = await service.PatchStatusAsync(
            _tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "resolved" });

        result.Should().NotBeNull();
        result!.Note.Should().BeNull();

        var audit = await _db.AuditLogs
            .Where(a => a.EntityType == "conversation" && a.EntityId == conv.Id)
            .FirstOrDefaultAsync();

        audit.Should().NotBeNull();
        using var newDoc = JsonDocument.Parse(audit!.NewValues!);
        newDoc.RootElement.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // TC-NOTE-03: PATCH con nota vacía → normaliza a null
    [Fact]
    public async Task PatchStatus_EmptyNote_NormalizesToNull()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("open");
        var service = BuildService();

        var result = await service.PatchStatusAsync(
            _tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "waiting_human", Note = "   " });

        result.Should().NotBeNull();
        result!.Note.Should().BeNull();

        var audit = await _db.AuditLogs
            .Where(a => a.EntityType == "conversation" && a.EntityId == conv.Id)
            .FirstOrDefaultAsync();

        audit.Should().NotBeNull();
        using var newDoc = JsonDocument.Parse(audit!.NewValues!);
        newDoc.RootElement.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // TC-NOTE-04: Tres cambios sucesivos → statusHistory devuelve 3 items desc
    [Fact]
    public async Task GetDetail_ThreeStatusChanges_ReturnsHistoryDescending()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("open");
        var service = BuildService();

        await service.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "waiting_human", Note = "Primera nota" });

        await service.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "open", Note = "Reactivar IA" });

        await service.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "resolved" });

        var detail = await service.GetConversationDetailAsync(_tenantId, conv.Id);

        detail.Should().NotBeNull();
        detail!.StatusHistory.Should().HaveCount(3);
        detail.StatusHistory[0].NewStatus.Should().Be("resolved");
        detail.StatusHistory[1].NewStatus.Should().Be("open");
        detail.StatusHistory[1].Note.Should().Be("Reactivar IA");
        detail.StatusHistory[2].NewStatus.Should().Be("waiting_human");
        detail.StatusHistory[2].Note.Should().Be("Primera nota");
    }

    // TC-NOTE-05: Aislamiento multi-tenant
    [Fact]
    public async Task GetDetail_DifferentTenant_ReturnsEmptyHistory()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("open");
        var service = BuildService();

        await service.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "waiting_human", Note = "Nota del tenant A" });

        var auditCount = await _db.AuditLogs
            .Where(a => a.TenantId == _tenantId && a.EntityId == conv.Id)
            .CountAsync();
        auditCount.Should().Be(1);

        var otherTenantId = Guid.NewGuid();
        var detailFromOtherTenant = await service.GetConversationDetailAsync(otherTenantId, conv.Id);

        detailFromOtherTenant.Should().BeNull();
    }

    // TC-NOTE-06: ActorId corresponde al UserId del context
    [Fact]
    public async Task PatchStatus_ActorId_MatchesContextUserId()
    {
        await SeedTenantAsync();
        var conv     = await SeedConversationAsync("open");
        var actorId  = Guid.NewGuid();
        var service  = BuildService(actorUserId: actorId);

        await service.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "waiting_human" });

        var audit = await _db.AuditLogs
            .Where(a => a.EntityType == "conversation" && a.EntityId == conv.Id)
            .FirstOrDefaultAsync();

        audit.Should().NotBeNull();
        audit!.ActorId.Should().Be(actorId);
    }

    // TC-NOTE-07: Conversación no encontrada → no se crea audit_log
    [Fact]
    public async Task PatchStatus_ConversationNotFound_NoAuditLog()
    {
        await SeedTenantAsync();
        var service = BuildService();

        var result = await service.PatchStatusAsync(
            _tenantId, Guid.NewGuid(),
            new PatchConversationStatusRequest { Status = "waiting_human", Note = "No debería guardarse" });

        result.Should().BeNull();

        var auditCount = await _db.AuditLogs
            .Where(a => a.TenantId == _tenantId && a.Action == "status_changed")
            .CountAsync();
        auditCount.Should().Be(0);
    }
}
