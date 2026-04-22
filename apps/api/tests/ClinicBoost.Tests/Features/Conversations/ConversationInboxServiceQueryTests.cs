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
// ConversationInboxServiceQueryTests  —  WQ-006
//
// Tests for read operations and status mutation:
//
//   GetInboxAsync
//     TC-INBOX-01  Returns conversations for the correct tenant
//     TC-INBOX-02  Filters by status
//     TC-INBOX-03  Filters by flowId
//     TC-INBOX-04  Search by patient name (case-insensitive)
//     TC-INBOX-05  Search by patient phone
//     TC-INBOX-06  Pagination (page 2)
//     TC-INBOX-07  WaitingHumanCount independent of filters
//     TC-INBOX-08  Empty result for wrong tenant (multi-tenant isolation)
//
//   GetConversationDetailAsync
//     TC-DETAIL-01  Returns conversation with messages in chronological order
//     TC-DETAIL-02  Returns null for wrong tenant (404)
//     TC-DETAIL-03  Returns null for nonexistent conversation
//     TC-DETAIL-04  Limits messages to 100
//
//   PatchStatusAsync
//     TC-PATCH-01  Changes status from open → waiting_human
//     TC-PATCH-02  Changes status from waiting_human → resolved, sets ResolvedAt
//     TC-PATCH-03  Invalid status → returns null
//     TC-PATCH-04  Wrong tenant → returns null
//
//   GetPendingHandoffAsync
//     TC-HANDOFF-01  Returns waiting_human conversations ordered by oldest first
//     TC-HANDOFF-02  Returns empty for tenant with no waiting_human
//     TC-HANDOFF-03  Multi-tenant isolation
// ════════════════════════════════════════════════════════════════════════════

public sealed class ConversationInboxServiceQueryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _tenantId  = Guid.NewGuid();
    private readonly Guid _tenantIdB = Guid.NewGuid();
    private readonly Guid _patientId = Guid.NewGuid();

    public ConversationInboxServiceQueryTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"inbox_query_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(opts);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedTenantAsync(Guid? tenantId = null, string name = "Clínica Test")
    {
        var tid = tenantId ?? _tenantId;
        _db.Tenants.Add(new Tenant
        {
            Id = tid, Name = name, Slug = $"slug-{tid:N}"[..20],
            WhatsAppNumber = "+34600000001", TimeZone = "Europe/Madrid",
        });
        await _db.SaveChangesAsync();
    }

    private async Task<Patient> SeedPatientAsync(
        Guid? tenantId = null, string name = "Ana García", string phone = "+34666000001")
    {
        var p = new Patient
        {
            TenantId = tenantId ?? _tenantId, FullName = name, Phone = phone,
            Status = PatientStatus.Active, RgpdConsent = true,
        };
        _db.Patients.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    private async Task<Conversation> SeedConversationAsync(
        Guid? tenantId = null, Guid? patientId = null,
        string status = "open", string flowId = "flow_00",
        DateTimeOffset? lastMessageAt = null)
    {
        var conv = new Conversation
        {
            TenantId = tenantId ?? _tenantId,
            PatientId = patientId ?? _patientId,
            Channel = "whatsapp", FlowId = flowId, Status = status,
            LastMessageAt = lastMessageAt,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();
        return conv;
    }

    private async Task<Message> SeedMessageAsync(
        Guid conversationId, Guid? tenantId = null,
        string direction = "inbound", string body = "Hola",
        DateTimeOffset? createdAt = null)
    {
        var msg = new Message
        {
            TenantId = tenantId ?? _tenantId,
            ConversationId = conversationId,
            Direction = direction, Channel = "whatsapp",
            Body = body, Status = direction == "inbound" ? "received" : "sent",
            GeneratedByAi = false,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();
        return msg;
    }

    private ConversationInboxService BuildService()
    {
        var tenantCtx = Substitute.For<ITenantContext>();
        tenantCtx.UserId.Returns(Guid.NewGuid());
        return new ConversationInboxService(
            _db,
            Substitute.For<IOutboundMessageSender>(),
            tenantCtx,
            NullLogger<ConversationInboxService>.Instance);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GetInboxAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetInbox_ReturnsConversationsForCorrectTenant()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        await SeedConversationAsync(patientId: patient.Id, status: "open");
        await SeedConversationAsync(patientId: patient.Id, status: "waiting_human");
        var sut = BuildService();

        var result = await sut.GetInboxAsync(_tenantId, new InboxQueryParams());

        result.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetInbox_FiltersByStatus()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        await SeedConversationAsync(patientId: patient.Id, status: "open");
        await SeedConversationAsync(patientId: patient.Id, status: "waiting_human");
        await SeedConversationAsync(patientId: patient.Id, status: "resolved");
        var sut = BuildService();

        var result = await sut.GetInboxAsync(_tenantId,
            new InboxQueryParams { Status = "waiting_human" });

        result.Items.Should().HaveCount(1);
        result.Items[0].Status.Should().Be("waiting_human");
    }

    [Fact]
    public async Task GetInbox_FiltersByFlowId()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        await SeedConversationAsync(patientId: patient.Id, flowId: "flow_00");
        await SeedConversationAsync(patientId: patient.Id, flowId: "flow_01");
        var sut = BuildService();

        var result = await sut.GetInboxAsync(_tenantId,
            new InboxQueryParams { FlowId = "flow_01" });

        result.Items.Should().HaveCount(1);
        result.Items[0].FlowId.Should().Be("flow_01");
    }

    [Fact]
    public async Task GetInbox_SearchByPatientName_CaseInsensitive()
    {
        await SeedTenantAsync();
        var p1 = await SeedPatientAsync(name: "Ana García López", phone: "+34666000001");
        var p2 = await SeedPatientAsync(name: "Pedro Martínez", phone: "+34666000002");
        await SeedConversationAsync(patientId: p1.Id);
        await SeedConversationAsync(patientId: p2.Id);
        var sut = BuildService();

        var result = await sut.GetInboxAsync(_tenantId,
            new InboxQueryParams { Search = "ana" });

        result.Items.Should().HaveCount(1);
        result.Items[0].PatientName.Should().Contain("Ana");
    }

    [Fact]
    public async Task GetInbox_SearchByPatientPhone()
    {
        await SeedTenantAsync();
        var p1 = await SeedPatientAsync(name: "Ana", phone: "+34666111222");
        var p2 = await SeedPatientAsync(name: "Pedro", phone: "+34666333444");
        await SeedConversationAsync(patientId: p1.Id);
        await SeedConversationAsync(patientId: p2.Id);
        var sut = BuildService();

        var result = await sut.GetInboxAsync(_tenantId,
            new InboxQueryParams { Search = "333444" });

        result.Items.Should().HaveCount(1);
        result.Items[0].PatientPhone.Should().Contain("333444");
    }

    [Fact]
    public async Task GetInbox_Pagination_Page2()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        for (int i = 0; i < 5; i++)
            await SeedConversationAsync(patientId: patient.Id);
        var sut = BuildService();

        var result = await sut.GetInboxAsync(_tenantId,
            new InboxQueryParams { Page = 2, PageSize = 3 });

        result.Page.Should().Be(2);
        result.PageSize.Should().Be(3);
        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(5);
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetInbox_WaitingHumanCount_IndependentOfFilters()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        await SeedConversationAsync(patientId: patient.Id, status: "open");
        await SeedConversationAsync(patientId: patient.Id, status: "waiting_human");
        await SeedConversationAsync(patientId: patient.Id, status: "waiting_human");
        var sut = BuildService();

        var result = await sut.GetInboxAsync(_tenantId,
            new InboxQueryParams { Status = "open" });

        result.Items.Should().HaveCount(1);
        result.WaitingHumanCount.Should().Be(2,
            "WaitingHumanCount counts all waiting_human regardless of status filter");
    }

    [Fact]
    public async Task GetInbox_WrongTenant_ReturnsEmpty()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        await SeedConversationAsync(patientId: patient.Id, status: "open");
        var sut = BuildService();

        var result = await sut.GetInboxAsync(Guid.NewGuid(), new InboxQueryParams());

        result.TotalCount.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GetConversationDetailAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetDetail_ReturnsMessagesInChronologicalOrder()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        var conv    = await SeedConversationAsync(patientId: patient.Id);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-3);
        await SeedMessageAsync(conv.Id, body: "Primero", createdAt: t0);
        await SeedMessageAsync(conv.Id, body: "Segundo", createdAt: t0.AddMinutes(1));
        await SeedMessageAsync(conv.Id, body: "Tercero", createdAt: t0.AddMinutes(2));
        var sut = BuildService();

        var result = await sut.GetConversationDetailAsync(_tenantId, conv.Id);

        result.Should().NotBeNull();
        result!.Messages.Should().HaveCount(3);
        result.Messages[0].Body.Should().Be("Primero");
        result.Messages[1].Body.Should().Be("Segundo");
        result.Messages[2].Body.Should().Be("Tercero");
        result.PatientName.Should().Be(patient.FullName);
    }

    [Fact]
    public async Task GetDetail_WrongTenant_ReturnsNull()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        var conv    = await SeedConversationAsync(patientId: patient.Id);
        var sut = BuildService();

        var result = await sut.GetConversationDetailAsync(Guid.NewGuid(), conv.Id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetDetail_NonexistentConversation_ReturnsNull()
    {
        await SeedTenantAsync();
        var sut = BuildService();

        var result = await sut.GetConversationDetailAsync(_tenantId, Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetDetail_LimitsTo100Messages()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        var conv    = await SeedConversationAsync(patientId: patient.Id);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-200);
        for (int i = 0; i < 120; i++)
            await SeedMessageAsync(conv.Id, body: $"Msg {i}", createdAt: t0.AddMinutes(i));
        var sut = BuildService();

        var result = await sut.GetConversationDetailAsync(_tenantId, conv.Id);

        result.Should().NotBeNull();
        result!.Messages.Should().HaveCount(100);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PatchStatusAsync (core status logic, not audit — audit is in NoteTests)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PatchStatus_OpenToWaitingHuman_UpdatesStatus()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        var conv    = await SeedConversationAsync(patientId: patient.Id, status: "open");
        var sut = BuildService();

        var result = await sut.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "waiting_human" });

        result.Should().NotBeNull();
        result!.PreviousStatus.Should().Be("open");
        result.NewStatus.Should().Be("waiting_human");

        var updated = await _db.Conversations.FindAsync(conv.Id);
        updated!.Status.Should().Be("waiting_human");
    }

    [Fact]
    public async Task PatchStatus_ToResolved_SetsResolvedAt()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        var conv    = await SeedConversationAsync(patientId: patient.Id, status: "waiting_human");
        var sut = BuildService();

        var result = await sut.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "resolved" });

        result.Should().NotBeNull();
        result!.NewStatus.Should().Be("resolved");

        var updated = await _db.Conversations.FindAsync(conv.Id);
        updated!.ResolvedAt.Should().NotBeNull();
        updated.ResolvedAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PatchStatus_InvalidStatus_ReturnsNull()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        var conv    = await SeedConversationAsync(patientId: patient.Id, status: "open");
        var sut = BuildService();

        var result = await sut.PatchStatusAsync(_tenantId, conv.Id,
            new PatchConversationStatusRequest { Status = "invalid_status" });

        result.Should().BeNull();

        var unchanged = await _db.Conversations.FindAsync(conv.Id);
        unchanged!.Status.Should().Be("open");
    }

    [Fact]
    public async Task PatchStatus_WrongTenant_ReturnsNull()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        var conv    = await SeedConversationAsync(patientId: patient.Id, status: "open");
        var sut = BuildService();

        var result = await sut.PatchStatusAsync(Guid.NewGuid(), conv.Id,
            new PatchConversationStatusRequest { Status = "waiting_human" });

        result.Should().BeNull();

        var unchanged = await _db.Conversations.FindAsync(conv.Id);
        unchanged!.Status.Should().Be("open");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GetPendingHandoffAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPendingHandoff_ReturnsWaitingHuman_OldestFirst()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();

        var c1 = await SeedConversationAsync(patientId: patient.Id, status: "waiting_human");
        c1.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var c2 = await SeedConversationAsync(patientId: patient.Id, status: "waiting_human");
        c2.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedConversationAsync(patientId: patient.Id, status: "open");
        await _db.SaveChangesAsync();

        var sut    = BuildService();
        var result = await sut.GetPendingHandoffAsync(_tenantId);

        result.Count.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items[0].ConversationId.Should().Be(c1.Id, "oldest first");
        result.Items[0].WaitingMinutes.Should().BeGreaterOrEqualTo(9);
    }

    [Fact]
    public async Task GetPendingHandoff_NoWaitingHuman_ReturnsEmpty()
    {
        await SeedTenantAsync();
        var patient = await SeedPatientAsync();
        await SeedConversationAsync(patientId: patient.Id, status: "open");
        await SeedConversationAsync(patientId: patient.Id, status: "resolved");
        var sut = BuildService();

        var result = await sut.GetPendingHandoffAsync(_tenantId);

        result.Count.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingHandoff_MultiTenantIsolation()
    {
        await SeedTenantAsync(_tenantId, "Clínica A");
        await SeedTenantAsync(_tenantIdB, "Clínica B");
        var pA = await SeedPatientAsync(_tenantId, "Paciente A", "+34666000011");
        var pB = await SeedPatientAsync(_tenantIdB, "Paciente B", "+34666000022");
        await SeedConversationAsync(tenantId: _tenantId, patientId: pA.Id, status: "waiting_human");
        await SeedConversationAsync(tenantId: _tenantIdB, patientId: pB.Id, status: "waiting_human");
        var sut = BuildService();

        var resultA = await sut.GetPendingHandoffAsync(_tenantId);
        var resultB = await sut.GetPendingHandoffAsync(_tenantIdB);

        resultA.Count.Should().Be(1);
        resultA.Items[0].PatientName.Should().Be("Paciente A");
        resultB.Count.Should().Be(1);
        resultB.Items[0].PatientName.Should().Be("Paciente B");
    }
}
