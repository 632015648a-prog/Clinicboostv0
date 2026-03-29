using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Infrastructure.Idempotency;

// ════════════════════════════════════════════════════════════════════════════
// IdempotencyServiceTests
//
// Tests de integración ligeros usando EF Core InMemory.
// Nota sobre InMemory vs Sqlite:
//   · InMemory no soporta SQL crudo (ExecuteSqlAsync) → el método
//     InsertIgnoringConflictAsync falla con NotSupportedException.
//   · Para tests con InMemory usamos un subclase testeable que permite
//     inyectar la lógica de inserción real vía delegado.
//   · En CI con Postgres real se pueden añadir tests de integración
//     completos en ClinicBoost.IntegrationTests (proyecto separado).
//
// ESTRATEGIA DE TEST AQUÍ:
//   1. Tests unitarios puros del IdempotencyResult → IdempotencyResultTests.cs
//   2. Tests del helper ComputeHash → IdempotencyServiceHashTests.cs
//   3. Tests del flujo completo con FakeIdempotencyService (doble de test) →
//      simula los tres escenarios (nuevo, duplicado, mismatch) con EF InMemory.
//
// De esta forma tenemos cobertura de la lógica sin depender de Postgres en CI.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Test double de IdempotencyService que reemplaza InsertIgnoringConflictAsync
/// con una implementación basada en EF Core InMemory (SELECT+INSERT no atómico,
/// suficiente para tests unitarios).
/// </summary>
internal sealed class TestableIdempotencyService : IdempotencyService
{
    private readonly AppDbContext _dbCtx;

    public TestableIdempotencyService(
        AppDbContext                db,
        ITenantContext              tenant,
        ILogger<IdempotencyService> logger)
        : base(db, tenant, logger)
    {
        _dbCtx = db;
    }

    /// <summary>
    /// Sobrescribe el INSERT SQL con una versión EF pura (SELECT + conditional INSERT).
    /// No es atómico, pero es suficiente para tests unitarios sin Postgres.
    ///
    /// NOTA: EF InMemory tiene limitaciones con Guid? nullable en comparaciones LINQ.
    /// Usamos .ToList() + LINQ to Objects para garantizar semántica correcta de null.
    /// </summary>
    protected override async Task<(bool inserted, ProcessedEvent? existing)>
        AtomicInsertAsync(
            ProcessedEvent    evt,
            CancellationToken ct)
    {
        // Cargar candidatos por (event_type, event_id) — sin filtrar por tenant_id
        // para evitar problemas de semántica nullable en EF InMemory.
        var candidates = await _dbCtx.ProcessedEvents
            .AsNoTracking()
            .Where(e => e.EventType == evt.EventType && e.EventId == evt.EventId)
            .ToListAsync(ct);

        // Filtrar por tenant_id con semántica null correcta en LINQ to Objects
        var existing = candidates.FirstOrDefault(e =>
            e.TenantId.Equals(evt.TenantId) ||
            (!e.TenantId.HasValue && !evt.TenantId.HasValue));

        if (existing is not null)
            return (false, existing);

        _dbCtx.ProcessedEvents.Add(evt);
        await _dbCtx.SaveChangesAsync(ct);
        return (true, null);
    }
}

public sealed class IdempotencyServiceTests : IDisposable
{
    // ── Infraestructura de test ───────────────────────────────────────────────

    private readonly AppDbContext  _db;
    private readonly ITenantContext _tenant;

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    public IdempotencyServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())  // BD aislada por test
            .Options;

        _db     = new AppDbContext(opts);
        _tenant = Substitute.For<ITenantContext>();
        _tenant.IsInitialized.Returns(false);
        _tenant.TenantId.Returns((Guid?)null);
    }

    public void Dispose() => _db.Dispose();

    private TestableIdempotencyService BuildSut() =>
        new(_db, _tenant, NullLogger<IdempotencyService>.Instance);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetTenantCtx(Guid tenantId)
    {
        _tenant.IsInitialized.Returns(true);
        _tenant.TenantId.Returns(tenantId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 1: Primer procesamiento
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_FirstTime_ReturnsShouldProcess()
    {
        var sut = BuildSut();

        var result = await sut.TryProcessAsync(
            eventType: "twilio.whatsapp_inbound",
            eventId:   "SM001",
            tenantId:  (Guid?)TenantA);

        result.ShouldProcess.Should().BeTrue("primer evento debe procesarse");
        result.AlreadyProcessed.Should().BeFalse();
        result.IsError.Should().BeFalse();
        result.ProcessedEventId.Should().NotBeNull();
        result.FirstProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryProcessAsync_FirstTime_PersistsToDatabase()
    {
        var sut = BuildSut();

        await sut.TryProcessAsync("twilio.voice_inbound", "CA001", tenantId: (Guid?)TenantA);

        var persisted = await _db.ProcessedEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e =>
                e.EventType == "twilio.voice_inbound" &&
                e.EventId   == "CA001");

        persisted.Should().NotBeNull();
        persisted!.TenantId.Should().Be(TenantA);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 2: Evento duplicado
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_SecondCall_ReturnsDuplicate()
    {
        var sut = BuildSut();
        const string eventType = "twilio.message_status";
        const string eventId   = "SM002";
        const string payload   = "{\"MessageStatus\":\"delivered\"}";

        // Primera llamada
        var first = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)TenantA, payload: payload);
        first.ShouldProcess.Should().BeTrue();

        // Segunda llamada (re-entrega idéntica)
        var second = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)TenantA, payload: payload);

        second.ShouldProcess.Should().BeFalse();
        second.AlreadyProcessed.Should().BeTrue();
        second.IsPayloadMismatch.Should().BeFalse();
        second.ShouldSkip.Should().BeTrue();
        second.ProcessedEventId.Should().Be(first.ProcessedEventId,
            "el ID del registro existente debe ser el mismo que el primero");
    }

    [Fact]
    public async Task TryProcessAsync_DuplicateWithNullPayload_ReturnsDuplicate()
    {
        var sut = BuildSut();

        await sut.TryProcessAsync("internal.appointment_reminder", "job-uuid-1", tenantId: (Guid?)TenantA);
        var second = await sut.TryProcessAsync("internal.appointment_reminder", "job-uuid-1", tenantId: (Guid?)TenantA);

        second.AlreadyProcessed.Should().BeTrue();
        second.IsPayloadMismatch.Should().BeFalse("null vs null no es mismatch");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 3: Payload mismatch (mismo ID, cuerpo diferente)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_DuplicateWithDifferentPayload_ReturnsPayloadMismatch()
    {
        var sut = BuildSut();
        const string eventType      = "twilio.whatsapp_inbound";
        const string eventId        = "SM003";
        const string originalBody   = "{\"Body\":\"Hola\",\"MessageSid\":\"SM003\"}";
        const string alteredBody    = "{\"Body\":\"Adios\",\"MessageSid\":\"SM003\"}";

        // Primera entrega
        var first = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)TenantA, payload: originalBody);
        first.ShouldProcess.Should().BeTrue();

        // Re-entrega con cuerpo alterado (posible replay attack)
        var second = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)TenantA, payload: alteredBody);

        second.AlreadyProcessed.Should().BeTrue();
        second.IsPayloadMismatch.Should().BeTrue(
            "mismo ID con payload distinto indica re-entrega alterada");
        second.ShouldProcess.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 4: Aislamiento multi-tenant
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_SameEventId_DifferentTenants_BothProcess()
    {
        var sut = BuildSut();
        const string eventType = "twilio.whatsapp_inbound";
        const string eventId   = "SM004";  // mismo SID de Twilio (improbable pero posible en multitenant)

        var resultA = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)TenantA);
        var resultB = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)TenantB);

        resultA.ShouldProcess.Should().BeTrue("tenant A debe procesar el evento");
        resultB.ShouldProcess.Should().BeTrue("tenant B debe procesar el evento independientemente");
        resultA.ProcessedEventId!.Value.Should().NotBe(resultB.ProcessedEventId!.Value,
            "cada tenant genera su propio registro");
    }

    [Fact]
    public async Task TryProcessAsync_SameEventId_NullVsRealTenant_BothProcess()
    {
        var sut = BuildSut();
        const string eventType = "twilio.voice_inbound";
        const string eventId   = "CA005";

        // Primero con tenant null (webhook global antes de resolver tenant)
        var resultNull = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)null);

        // Luego con tenant resuelto — debe ser evento distinto
        var resultA = await sut.TryProcessAsync(eventType, eventId, tenantId: (Guid?)TenantA);

        resultNull.ShouldProcess.Should().BeTrue();
        resultA.ShouldProcess.Should().BeTrue("null y TenantA son claves distintas");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 5: TenantContext automático
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_WithoutExplicitTenantId_UsesTenantContext()
    {
        SetTenantCtx(TenantA);
        var sut = BuildSut();

        var result = await sut.TryProcessAsync(
            eventType: "internal.billing_run",
            eventId:   "run-001");

        result.ShouldProcess.Should().BeTrue();

        var persisted = await _db.ProcessedEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == "run-001");

        persisted.Should().NotBeNull();
        persisted!.TenantId.Should().Be(TenantA,
            "el tenant se resuelve automáticamente del ITenantContext");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 6: Sobrecarga tipada TryProcessAsync<T>
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_Typed_SerializesPayloadForHash()
    {
        var sut = BuildSut();

        var payload1 = new { MessageSid = "SM006", Body = "Hola" };
        var payload2 = new { MessageSid = "SM006", Body = "Hola" };  // mismo contenido

        var r1 = await sut.TryProcessAsync("twilio.whatsapp_inbound", "SM006", payload1, tenantId: (Guid?)TenantA);
        var r2 = await sut.TryProcessAsync("twilio.whatsapp_inbound", "SM006", payload2, tenantId: (Guid?)TenantA);

        r1.ShouldProcess.Should().BeTrue();
        r2.AlreadyProcessed.Should().BeTrue();
        r2.IsPayloadMismatch.Should().BeFalse("payloads serializados idénticos = duplicado legítimo");
    }

    [Fact]
    public async Task TryProcessAsync_Typed_DifferentPayloadObjects_DetectsMismatch()
    {
        var sut = BuildSut();

        var original = new { MessageSid = "SM007", Body = "Original" };
        var altered  = new { MessageSid = "SM007", Body = "Alterado" };

        await sut.TryProcessAsync("twilio.whatsapp_inbound", "SM007", original, tenantId: (Guid?)TenantA);
        var second = await sut.TryProcessAsync("twilio.whatsapp_inbound", "SM007", altered, tenantId: (Guid?)TenantA);

        second.IsPayloadMismatch.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 7: IsAlreadyProcessedAsync
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IsAlreadyProcessedAsync_ReturnsFalse_ForNewEvent()
    {
        var sut = BuildSut();

        var result = await sut.IsAlreadyProcessedAsync(
            "twilio.voice_status", "CA010", tenantId: (Guid?)TenantA);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsAlreadyProcessedAsync_ReturnsTrue_AfterProcessing()
    {
        var sut = BuildSut();

        await sut.TryProcessAsync("twilio.voice_status", "CA011", tenantId: (Guid?)TenantA);

        var result = await sut.IsAlreadyProcessedAsync(
            "twilio.voice_status", "CA011", tenantId: (Guid?)TenantA);

        result.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 8: Validación de argumentos
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("", "SM001")]
    [InlineData("  ", "SM001")]
    [InlineData("twilio.msg", "")]
    [InlineData("twilio.msg", "  ")]
    public async Task TryProcessAsync_InvalidArgs_ThrowsArgumentException(
        string eventType, string eventId)
    {
        var sut = BuildSut();

        var act = async () => await sut.TryProcessAsync(eventType, eventId);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 9: Concurrencia simulada
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_ConcurrentCalls_OnlyOneSucceeds()
    {
        // EF InMemory no garantiza thread-safety en writes concurrentes,
        // pero verifica que la lógica de deduplicación funciona en serie.
        // Para concurrencia real, el test de integración con Postgres es el adecuado.
        var sut = BuildSut();

        // Dos llamadas secuenciales simulando concurrencia resuelta por orden
        var r1 = await sut.TryProcessAsync("internal.reminder", "job-concurrent-001", tenantId: (Guid?)TenantA);
        var r2 = await sut.TryProcessAsync("internal.reminder", "job-concurrent-001", tenantId: (Guid?)TenantA);

        r1.ShouldProcess.Should().BeTrue("primera llamada debe procesar");
        r2.AlreadyProcessed.Should().BeTrue("segunda llamada debe detectar duplicado");

        var count = await _db.ProcessedEvents
            .CountAsync(e => e.EventId == "job-concurrent-001");
        count.Should().Be(1, "solo un registro debe existir");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GRUPO 10: Eventos de distintos tipos no colisionan
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryProcessAsync_SameEventId_DifferentEventTypes_BothProcess()
    {
        var sut = BuildSut();
        const string eventId = "SHARED-ID-001";

        var voice     = await sut.TryProcessAsync("twilio.voice_inbound",     eventId, tenantId: (Guid?)TenantA);
        var whatsapp  = await sut.TryProcessAsync("twilio.whatsapp_inbound",  eventId, tenantId: (Guid?)TenantA);
        var smsStatus = await sut.TryProcessAsync("twilio.message_status",    eventId, tenantId: (Guid?)TenantA);
        var internal1 = await sut.TryProcessAsync("internal.appointment_reminder", eventId, tenantId: (Guid?)TenantA);

        voice.ShouldProcess.Should().BeTrue();
        whatsapp.ShouldProcess.Should().BeTrue();
        smsStatus.ShouldProcess.Should().BeTrue();
        internal1.ShouldProcess.Should().BeTrue();

        var totalCount = await _db.ProcessedEvents
            .CountAsync(e => e.EventId == eventId);
        totalCount.Should().Be(4, "cada event_type genera un registro independiente");
    }
}
