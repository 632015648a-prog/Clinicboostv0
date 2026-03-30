using ClinicBoost.Api.Features.Appointments;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Automation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Appointments;

// ════════════════════════════════════════════════════════════════════════════
// AppointmentServiceTests
//
// Tests unitarios de AppointmentService sobre EF InMemory.
//
// ESCENARIOS CUBIERTOS
// ─────────────────────
// GetAvailableSlots:
//   · Devuelve slots para tenant con timezone Europe/Madrid
//   · Excluye slots ocupados por citas existentes
//   · No devuelve slots en el pasado
//   · Respeta horario laboral (L-V 09:00-18:00)
//
// BookAppointment:
//   · Flujo feliz: crea Appointment + AppointmentEvent
//   · Race condition / slot ya ocupado: devuelve SlotConflict
//   · Idempotencia: segunda llamada con mismo key devuelve la misma cita
//   · Revenue telemetry: crea RevenueEvent cuando Source != Manual y hay SessionAmount
//   · DiscountExceeded: descuento mayor al máximo configurado
//
// CancelAppointment:
//   · Cancela cita Scheduled con AppointmentEvent
//   · Registra RevenueEvent de pérdida si era recovered
//   · Falla si la cita no existe (NOT_FOUND)
//   · Falla si el estado no permite cancelación (INVALID_STATUS)
//   · Falla si ActorType es inválido (INVALID_ACTOR)
//
// RescheduleAppointment:
//   · Flujo feliz: cancela original + crea nueva + AppointmentEvents + RevenueEvent
//   · Race condition en nuevo slot: devuelve SlotConflict
//   · Idempotencia: segunda llamada devuelve la cita ya creada
//   · Falla si la cita original no existe
//   · Falla si el estado no permite reprogramación
// ════════════════════════════════════════════════════════════════════════════

public sealed class AppointmentServiceTests
{
    private static readonly Guid TenantId  = Guid.NewGuid();
    private static readonly Guid PatientId = Guid.NewGuid();

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("AppointmentTests_" + Guid.NewGuid().ToString("N"))
            // EF InMemory no soporta transacciones con IsolationLevel;
            // se ignora el warning para que los tests funcionen igual que en Postgres.
            // El comportamiento transaccional real se valida en integration tests con Postgres.
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AppointmentService CreateService(
        AppDbContext        db,
        IIdempotencyService? idempotency = null)
    {
        var idp = idempotency ?? CreateNewEventIdempotency();
        return new AppointmentService(
            db,
            idp,
            NullLogger<AppointmentService>.Instance);
    }

    /// <summary>
    /// Crea un mock de IIdempotencyService que siempre devuelve "evento nuevo"
    /// (no hay duplicados). Comportamiento por defecto en la mayoría de tests.
    /// </summary>
    private static IIdempotencyService CreateNewEventIdempotency()
    {
        var idp = Substitute.For<IIdempotencyService>();
        idp.TryProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(),  Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
        return idp;
    }

    /// <summary>
    /// Crea un mock de IIdempotencyService que devuelve "ya procesado" (duplicado).
    /// </summary>
    private static IIdempotencyService CreateDuplicateIdempotency()
    {
        var idp = Substitute.For<IIdempotencyService>();
        idp.TryProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(),  Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.Duplicate(Guid.NewGuid(), DateTimeOffset.UtcNow));
        return idp;
    }

    private static void SeedTenant(AppDbContext db, string tz = "Europe/Madrid")
    {
        db.Tenants.Add(new ClinicBoost.Domain.Tenants.Tenant
        {
            Id             = TenantId,
            Name           = "Clínica Test",
            Slug           = "clinica-test",
            TimeZone       = tz,
            WhatsAppNumber = "+34910000001",
        });
        db.SaveChanges();
    }

    private static Appointment SeedAppointment(
        AppDbContext      db,
        DateTimeOffset?   startsAt   = null,
        AppointmentStatus status     = AppointmentStatus.Scheduled,
        string            therapist  = "Dr. García",
        bool              isRecovered = false,
        decimal?          revenue    = null)
    {
        var start = startsAt ?? DateTimeOffset.UtcNow.AddDays(1);
        var appt  = new Appointment
        {
            TenantId         = TenantId,
            PatientId        = PatientId,
            TherapistName    = therapist,
            StartsAtUtc      = start,
            EndsAtUtc        = start.AddHours(1),
            Status           = status,
            Source           = isRecovered ? AppointmentSource.WhatsApp : AppointmentSource.Manual,
            IsRecovered      = isRecovered,
            RecoveredRevenue = revenue,
        };
        db.Appointments.Add(appt);
        db.SaveChanges();
        return appt;
    }

    private static void SeedDiscountRule(AppDbContext db, decimal maxPct)
    {
        db.RuleConfigs.Add(new RuleConfig
        {
            TenantId    = TenantId,
            FlowId      = "global",
            RuleKey     = "discount_max_pct",
            RuleValue   = maxPct.ToString(),
            ValueType   = "decimal",
            IsActive    = true,
        });
        db.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GetAvailableSlots
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAvailableSlots_ReturnsSlots_WhenTenantExists()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);
        var svc = CreateService(db);
        var req = new GetAvailableSlotsRequest
        {
            DateFrom       = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            DateTo         = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
            DurationMinutes = 60,
        };

        // Act
        var result = await svc.GetAvailableSlotsAsync(TenantId, req);

        // Assert
        result.Should().NotBeNull();
        result.Slots.Count.Should().BeGreaterThan(0);
        result.TimeZone.Should().Be("Europe/Madrid");
        result.Slots.Should().AllSatisfy(s =>
        {
            s.StartsAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
            s.EndsAtUtc.Should().BeAfter(s.StartsAtUtc);
            s.StartsAtLocal.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public async Task GetAvailableSlots_ExcludesOccupiedSlots()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);

        // Ocupar un slot específico mañana 09:00 UTC+1 → 08:00 UTC
        var tomorrow   = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var bookedStart = new DateTimeOffset(
            tomorrow.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);  // 09:00 Madrid = 08:00 UTC

        SeedAppointment(db, startsAt: bookedStart, therapist: "Dr. García");

        var svc = CreateService(db);
        var req = new GetAvailableSlotsRequest
        {
            DateFrom       = tomorrow.ToString("yyyy-MM-dd"),
            DateTo         = tomorrow.ToString("yyyy-MM-dd"),
            TherapistName  = "Dr. García",
            DurationMinutes = 60,
        };

        // Act
        var result = await svc.GetAvailableSlotsAsync(TenantId, req);

        // Assert
        // El slot ocupado no debe aparecer
        result.Slots.Should().NotContain(s =>
            s.StartsAtUtc == bookedStart && s.TherapistName == "Dr. García");
    }

    [Fact]
    public async Task GetAvailableSlots_UsesDefaultTimezone_WhenTenantNotFound()
    {
        // Arrange — tenant sin seed; el servicio usa "Europe/Madrid" por defecto
        using var db = CreateDb();
        var svc = CreateService(db);
        var req = new GetAvailableSlotsRequest
        {
            DateFrom       = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            DateTo         = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            DurationMinutes = 60,
        };

        // Act
        var result = await svc.GetAvailableSlotsAsync(Guid.NewGuid(), req);

        // Assert
        result.TimeZone.Should().Be("Europe/Madrid");
    }

    // ════════════════════════════════════════════════════════════════════════
    // BookAppointment — flujo feliz
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BookAppointment_CreatesAppointmentAndEvent_WhenSlotFree()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);
        var svc     = CreateService(db);
        var start   = DateTimeOffset.UtcNow.AddDays(2).AddHours(9);
        var request = new BookAppointmentRequest
        {
            PatientId      = PatientId,
            TherapistName  = "Dr. García",
            StartsAtUtc    = start,
            EndsAtUtc      = start.AddHours(1),
            Source         = AppointmentSource.WhatsApp,
            FlowId         = "flow_00",
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        response.Should().NotBeNull();
        response!.Status.Should().Be("scheduled");
        response.TherapistName.Should().Be("Dr. García");
        response.StartsAtLocal.Should().NotBeNullOrWhiteSpace();

        // Appointment en BD
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == response.AppointmentId);
        appt.Should().NotBeNull();
        appt!.PatientId.Should().Be(PatientId);
        appt.Status.Should().Be(AppointmentStatus.Scheduled);
        appt.IsRecovered.Should().BeTrue();   // Source = WhatsApp → recovered

        // AppointmentEvent en BD
        var evt = await db.AppointmentEvents
            .FirstOrDefaultAsync(e => e.AppointmentId == response.AppointmentId);
        evt.Should().NotBeNull();
        evt!.EventType.Should().Be("created");
        evt.ActorType.Should().Be("ai");       // Source != Manual → "ai"
        evt.FlowId.Should().Be("flow_00");
    }

    [Fact]
    public async Task BookAppointment_ManualSource_SetsTherapistActor()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);
        var svc   = CreateService(db);
        var start = DateTimeOffset.UtcNow.AddDays(2).AddHours(10);

        var request = new BookAppointmentRequest
        {
            PatientId     = PatientId,
            TherapistName = "Dr. García",
            StartsAtUtc   = start,
            EndsAtUtc     = start.AddHours(1),
            Source        = AppointmentSource.Manual,
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        var evt = await db.AppointmentEvents
            .FirstOrDefaultAsync(e => e.AppointmentId == response!.AppointmentId);
        evt!.ActorType.Should().Be("therapist");  // Manual → "therapist"

        var appt = await db.Appointments.FirstAsync(a => a.Id == response!.AppointmentId);
        appt.IsRecovered.Should().BeFalse();       // Manual → no recovered
    }

    // ════════════════════════════════════════════════════════════════════════
    // BookAppointment — race condition / slot ocupado
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BookAppointment_ReturnsSlotConflict_WhenOverlapExists()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);

        // Slot ya ocupado: 09:00–10:00 UTC
        var start = DateTimeOffset.UtcNow.AddDays(2).AddHours(9);
        SeedAppointment(db, startsAt: start, therapist: "Dr. García");

        var svc     = CreateService(db);
        var request = new BookAppointmentRequest
        {
            PatientId     = PatientId,
            TherapistName = "Dr. García",
            StartsAtUtc   = start.AddMinutes(30),   // overlap: 09:30–10:30 vs 09:00–10:00
            EndsAtUtc     = start.AddMinutes(90),
            Source        = AppointmentSource.WhatsApp,
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().NotBeNull();
        error!.Code.Should().Be("SLOT_CONFLICT");
        response.Should().BeNull();

        // No se debe haber creado ningún appointment nuevo
        var count = await db.Appointments.CountAsync(a =>
            a.TenantId == TenantId && a.StartsAtUtc == start.AddMinutes(30));
        count.Should().Be(0);
    }

    [Fact]
    public async Task BookAppointment_AllowsSimultaneous_DifferentTherapist()
    {
        // Arrange — mismo slot pero terapeuta diferente → no hay conflicto
        using var db = CreateDb();
        SeedTenant(db);
        var start = DateTimeOffset.UtcNow.AddDays(2).AddHours(9);
        SeedAppointment(db, startsAt: start, therapist: "Dr. García");

        var svc     = CreateService(db);
        var request = new BookAppointmentRequest
        {
            PatientId     = Guid.NewGuid(),
            TherapistName = "Dra. Martínez",   // diferente terapeuta
            StartsAtUtc   = start,
            EndsAtUtc     = start.AddHours(1),
            Source        = AppointmentSource.WhatsApp,
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        response.Should().NotBeNull();
    }

    // ════════════════════════════════════════════════════════════════════════
    // BookAppointment — revenue telemetry
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BookAppointment_CreatesRevenueEvent_WhenSourceIsWhatsApp()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);
        var svc   = CreateService(db);
        var start = DateTimeOffset.UtcNow.AddDays(2).AddHours(11);

        var request = new BookAppointmentRequest
        {
            PatientId      = PatientId,
            TherapistName  = "Dr. García",
            StartsAtUtc    = start,
            EndsAtUtc      = start.AddHours(1),
            Source         = AppointmentSource.WhatsApp,
            FlowId         = "flow_00",
            SessionAmount  = 60.00m,
            DiscountPct    = null,
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        response!.RevenueTracked.Should().BeTrue();

        var revenueEvent = await db.RevenueEvents
            .FirstOrDefaultAsync(r => r.AppointmentId == response.AppointmentId);
        revenueEvent.Should().NotBeNull();
        revenueEvent!.EventType.Should().Be("missed_call_converted");
        revenueEvent.Amount.Should().Be(60.00m);
        revenueEvent.SuccessFeeAmount.Should().BeApproximately(9.00m, 0.01m);  // 15% de 60
        revenueEvent.IsSuccessFeeEligible.Should().BeTrue();
        revenueEvent.FlowId.Should().Be("flow_00");
        revenueEvent.Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task BookAppointment_RevenueHasDiscountApplied_WhenDiscountPctProvided()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);
        SeedDiscountRule(db, maxPct: 20);  // permitir hasta 20%

        var svc   = CreateService(db);
        var start = DateTimeOffset.UtcNow.AddDays(2).AddHours(12);

        var request = new BookAppointmentRequest
        {
            PatientId     = PatientId,
            TherapistName = "Dr. García",
            StartsAtUtc   = start,
            EndsAtUtc     = start.AddHours(1),
            Source        = AppointmentSource.WhatsApp,
            SessionAmount = 60.00m,
            DiscountPct   = 10m,   // 10% descuento
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        var rev = await db.RevenueEvents
            .FirstOrDefaultAsync(r => r.AppointmentId == response!.AppointmentId);
        rev.Should().NotBeNull();
        rev!.Amount.Should().Be(54.00m);       // 60 * 0.9
        rev.OriginalAmount.Should().Be(60.00m);
        rev.DiscountPct.Should().Be(10m);
    }

    [Fact]
    public async Task BookAppointment_DoesNotCreateRevenueEvent_WhenSourceIsManual()
    {
        // Arrange — Manual → no recovered → no revenue
        using var db = CreateDb();
        SeedTenant(db);
        var svc   = CreateService(db);
        var start = DateTimeOffset.UtcNow.AddDays(2).AddHours(13);

        var request = new BookAppointmentRequest
        {
            PatientId      = PatientId,
            TherapistName  = "Dr. García",
            StartsAtUtc    = start,
            EndsAtUtc      = start.AddHours(1),
            Source         = AppointmentSource.Manual,
            SessionAmount  = 60.00m,
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        response!.RevenueTracked.Should().BeFalse();
        var revCount = await db.RevenueEvents.CountAsync(r =>
            r.AppointmentId == response.AppointmentId);
        revCount.Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════════════════
    // BookAppointment — validación de descuento
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BookAppointment_ReturnsDiscountExceeded_WhenDiscountTooHigh()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);
        SeedDiscountRule(db, maxPct: 10);   // máx 10%

        var svc   = CreateService(db);
        var start = DateTimeOffset.UtcNow.AddDays(2).AddHours(14);

        var request = new BookAppointmentRequest
        {
            PatientId     = PatientId,
            TherapistName = "Dr. García",
            StartsAtUtc   = start,
            EndsAtUtc     = start.AddHours(1),
            Source        = AppointmentSource.WhatsApp,
            SessionAmount = 60.00m,
            DiscountPct   = 25m,   // supera el máximo
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert
        error.Should().NotBeNull();
        error!.Code.Should().Be("DISCOUNT_EXCEEDED");
        response.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════════
    // BookAppointment — idempotencia
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BookAppointment_ReturnsExistingAppointment_WhenIdempotencyKeyDuplicated()
    {
        // Arrange
        using var db = CreateDb();
        SeedTenant(db);

        // Simular que ya existe el appointment para ese slot
        var start    = DateTimeOffset.UtcNow.AddDays(2).AddHours(15);
        var existing = SeedAppointment(db, startsAt: start, therapist: "Dr. García");

        // IdempotencyService devuelve "ya procesado"
        var svc = CreateService(db, CreateDuplicateIdempotency());

        var request = new BookAppointmentRequest
        {
            PatientId      = PatientId,
            TherapistName  = "Dr. García",
            StartsAtUtc    = start,
            EndsAtUtc      = start.AddHours(1),
            Source         = AppointmentSource.WhatsApp,
            IdempotencyKey = "idk-test-001",
        };

        // Act
        var (response, error) = await svc.BookAppointmentAsync(TenantId, request);

        // Assert — devuelve la cita existente sin crear duplicado
        error.Should().BeNull();
        response.Should().NotBeNull();
        response!.AppointmentId.Should().Be(existing.Id);

        var count = await db.Appointments.CountAsync(a =>
            a.TenantId     == TenantId &&
            a.StartsAtUtc  == start    &&
            a.TherapistName == "Dr. García");
        count.Should().Be(1);  // no se creó duplicado
    }

    // ════════════════════════════════════════════════════════════════════════
    // CancelAppointment
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelAppointment_CancelsAppointment_AndInsertsEvent()
    {
        // Arrange
        using var db  = CreateDb();
        SeedTenant(db);
        var start = DateTimeOffset.UtcNow.AddDays(3).AddHours(9);
        var appt  = SeedAppointment(db, startsAt: start);
        var svc   = CreateService(db);

        var request = new CancelAppointmentRequest
        {
            AppointmentId = appt.Id,
            ActorType     = "patient",
            Reason        = "Tengo otro compromiso",
            FlowId        = "flow_00",
        };

        // Act
        var (response, error) = await svc.CancelAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        response.Should().NotBeNull();
        response!.Status.Should().Be("cancelled");

        var updated = await db.Appointments.FirstAsync(a => a.Id == appt.Id);
        updated.Status.Should().Be(AppointmentStatus.Cancelled);

        var evt = await db.AppointmentEvents
            .FirstOrDefaultAsync(e =>
                e.AppointmentId == appt.Id && e.EventType == "cancelled");
        evt.Should().NotBeNull();
        evt!.ActorType.Should().Be("patient");
        evt.FlowId.Should().Be("flow_00");
    }

    [Fact]
    public async Task CancelAppointment_CreatesRevenueLoss_WhenAppointmentWasRecovered()
    {
        // Arrange
        using var db  = CreateDb();
        SeedTenant(db);
        var start = DateTimeOffset.UtcNow.AddDays(3).AddHours(10);
        var appt  = SeedAppointment(db, startsAt: start, isRecovered: true, revenue: 60.00m);
        var svc   = CreateService(db);

        var request = new CancelAppointmentRequest
        {
            AppointmentId = appt.Id,
            ActorType     = "patient",
        };

        // Act
        var (response, error) = await svc.CancelAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();

        var revLoss = await db.RevenueEvents
            .FirstOrDefaultAsync(r =>
                r.AppointmentId == appt.Id && r.EventType == "cancellation_loss");
        revLoss.Should().NotBeNull();
        revLoss!.Amount.Should().Be(-60.00m);         // negativo = pérdida
        revLoss.OriginalAmount.Should().Be(60.00m);
        revLoss.IsSuccessFeeEligible.Should().BeFalse();
    }

    [Fact]
    public async Task CancelAppointment_ReturnsNotFound_WhenAppointmentDoesNotExist()
    {
        using var db  = CreateDb();
        SeedTenant(db);
        var svc = CreateService(db);

        var (response, error) = await svc.CancelAppointmentAsync(TenantId, new CancelAppointmentRequest
        {
            AppointmentId = Guid.NewGuid(),
            ActorType     = "patient",
        });

        error.Should().NotBeNull();
        error!.Code.Should().Be("NOT_FOUND");
        response.Should().BeNull();
    }

    [Fact]
    public async Task CancelAppointment_ReturnsInvalidStatus_WhenAlreadyCancelled()
    {
        using var db  = CreateDb();
        SeedTenant(db);
        var appt = SeedAppointment(db, status: AppointmentStatus.Cancelled);
        var svc  = CreateService(db);

        var (response, error) = await svc.CancelAppointmentAsync(TenantId, new CancelAppointmentRequest
        {
            AppointmentId = appt.Id,
            ActorType     = "patient",
        });

        error.Should().NotBeNull();
        error!.Code.Should().Be("INVALID_STATUS");
    }

    [Fact]
    public async Task CancelAppointment_ReturnsInvalidActor_WhenActorTypeIsUnknown()
    {
        using var db  = CreateDb();
        SeedTenant(db);
        var appt = SeedAppointment(db);
        var svc  = CreateService(db);

        var (response, error) = await svc.CancelAppointmentAsync(TenantId, new CancelAppointmentRequest
        {
            AppointmentId = appt.Id,
            ActorType     = "robot",   // inválido
        });

        error.Should().NotBeNull();
        error!.Code.Should().Be("INVALID_ACTOR");
    }

    [Fact]
    public async Task CancelAppointment_DoesNotCreateRevenueLoss_WhenNotRecovered()
    {
        // Cita Manual → no recovered → no revenue loss
        using var db  = CreateDb();
        SeedTenant(db);
        var appt = SeedAppointment(db, isRecovered: false);
        var svc  = CreateService(db);

        await svc.CancelAppointmentAsync(TenantId, new CancelAppointmentRequest
        {
            AppointmentId = appt.Id,
            ActorType     = "patient",
        });

        var revCount = await db.RevenueEvents.CountAsync(r => r.AppointmentId == appt.Id);
        revCount.Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════════════════
    // RescheduleAppointment
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RescheduleAppointment_CancelsOldAndCreatesNew_WithEvents()
    {
        // Arrange
        using var db  = CreateDb();
        SeedTenant(db);
        var oldStart = DateTimeOffset.UtcNow.AddDays(4).AddHours(9);
        var original = SeedAppointment(db, startsAt: oldStart, therapist: "Dr. García",
            isRecovered: true, revenue: 60.00m);
        var svc      = CreateService(db);
        var newStart = DateTimeOffset.UtcNow.AddDays(5).AddHours(10);

        var request = new RescheduleAppointmentRequest
        {
            AppointmentId  = original.Id,
            TherapistName  = "Dr. García",
            NewStartsAtUtc = newStart,
            NewEndsAtUtc   = newStart.AddHours(1),
            ActorType      = "patient",
            Reason         = "Prefiero el miércoles",
            FlowId         = "flow_00",
        };

        // Act
        var (response, error) = await svc.RescheduleAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();
        response.Should().NotBeNull();
        response!.OldAppointmentId.Should().Be(original.Id);
        response.Status.Should().Be("scheduled");
        response.StartsAtLocal.Should().NotBeNullOrWhiteSpace();

        // Cita original cancelada
        var oldAppt = await db.Appointments.FirstAsync(a => a.Id == original.Id);
        oldAppt.Status.Should().Be(AppointmentStatus.Cancelled);

        // Nueva cita creada
        var newAppt = await db.Appointments.FirstAsync(a => a.Id == response.NewAppointmentId);
        newAppt.StartsAtUtc.Should().Be(newStart);
        newAppt.RescheduledFromId.Should().Be(original.Id);
        newAppt.Source.Should().Be(AppointmentSource.Rescheduled);
        newAppt.IsRecovered.Should().BeTrue();     // hereda de la original

        // AppointmentEvents: rescheduled_out en original + created en nueva
        var evtOld = await db.AppointmentEvents
            .FirstOrDefaultAsync(e => e.AppointmentId == original.Id && e.EventType == "rescheduled_out");
        evtOld.Should().NotBeNull();

        var evtNew = await db.AppointmentEvents
            .FirstOrDefaultAsync(e => e.AppointmentId == response.NewAppointmentId && e.EventType == "created");
        evtNew.Should().NotBeNull();
    }

    [Fact]
    public async Task RescheduleAppointment_CreatesRevenueSaved_WhenRecovered()
    {
        // Arrange
        using var db  = CreateDb();
        SeedTenant(db);
        var original = SeedAppointment(db,
            startsAt: DateTimeOffset.UtcNow.AddDays(4).AddHours(9),
            isRecovered: true,
            revenue: 60.00m);
        var svc      = CreateService(db);
        var newStart = DateTimeOffset.UtcNow.AddDays(6).AddHours(9);

        var request = new RescheduleAppointmentRequest
        {
            AppointmentId  = original.Id,
            TherapistName  = "Dr. García",
            NewStartsAtUtc = newStart,
            NewEndsAtUtc   = newStart.AddHours(1),
            ActorType      = "patient",
            FlowId         = "flow_07",
        };

        // Act
        var (response, error) = await svc.RescheduleAppointmentAsync(TenantId, request);

        // Assert
        error.Should().BeNull();

        var rev = await db.RevenueEvents
            .FirstOrDefaultAsync(r =>
                r.AppointmentId == response!.NewAppointmentId && r.EventType == "reschedule_saved");
        rev.Should().NotBeNull();
        rev!.Amount.Should().Be(60.00m);
        rev.SuccessFeeAmount.Should().BeApproximately(9.00m, 0.01m);
        rev.IsSuccessFeeEligible.Should().BeTrue();
    }

    [Fact]
    public async Task RescheduleAppointment_ReturnsSlotConflict_WhenNewSlotOccupied()
    {
        // Arrange
        using var db  = CreateDb();
        SeedTenant(db);
        var original = SeedAppointment(db,
            startsAt: DateTimeOffset.UtcNow.AddDays(4).AddHours(9),
            therapist: "Dr. García");

        // Ocupar el nuevo slot
        var newStart = DateTimeOffset.UtcNow.AddDays(5).AddHours(10);
        SeedAppointment(db, startsAt: newStart, therapist: "Dr. García");

        var svc = CreateService(db);

        var request = new RescheduleAppointmentRequest
        {
            AppointmentId  = original.Id,
            TherapistName  = "Dr. García",
            NewStartsAtUtc = newStart.AddMinutes(30),   // overlap
            NewEndsAtUtc   = newStart.AddMinutes(90),
            ActorType      = "patient",
        };

        // Act
        var (response, error) = await svc.RescheduleAppointmentAsync(TenantId, request);

        // Assert
        error.Should().NotBeNull();
        error!.Code.Should().Be("SLOT_CONFLICT");
        response.Should().BeNull();

        // La cita original debe seguir en estado Scheduled
        var orig = await db.Appointments.FirstAsync(a => a.Id == original.Id);
        orig.Status.Should().Be(AppointmentStatus.Scheduled);
    }

    [Fact]
    public async Task RescheduleAppointment_ReturnsNotFound_WhenOriginalNotExists()
    {
        using var db  = CreateDb();
        SeedTenant(db);
        var svc = CreateService(db);

        var (response, error) = await svc.RescheduleAppointmentAsync(TenantId,
            new RescheduleAppointmentRequest
            {
                AppointmentId  = Guid.NewGuid(),
                TherapistName  = "Dr. García",
                NewStartsAtUtc = DateTimeOffset.UtcNow.AddDays(5).AddHours(9),
                NewEndsAtUtc   = DateTimeOffset.UtcNow.AddDays(5).AddHours(10),
                ActorType      = "patient",
            });

        error.Should().NotBeNull();
        error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task RescheduleAppointment_ReturnsInvalidStatus_WhenOriginalCancelled()
    {
        using var db  = CreateDb();
        SeedTenant(db);
        var appt = SeedAppointment(db, status: AppointmentStatus.Cancelled);
        var svc  = CreateService(db);

        var (response, error) = await svc.RescheduleAppointmentAsync(TenantId,
            new RescheduleAppointmentRequest
            {
                AppointmentId  = appt.Id,
                TherapistName  = "Dr. García",
                NewStartsAtUtc = DateTimeOffset.UtcNow.AddDays(5).AddHours(9),
                NewEndsAtUtc   = DateTimeOffset.UtcNow.AddDays(5).AddHours(10),
                ActorType      = "patient",
            });

        error.Should().NotBeNull();
        error!.Code.Should().Be("INVALID_STATUS");
    }

    [Fact]
    public async Task RescheduleAppointment_Idempotent_WhenKeyDuplicated()
    {
        // Arrange — simular que ya existe la cita reprogramada
        using var db     = CreateDb();
        SeedTenant(db);
        var original = SeedAppointment(db,
            startsAt: DateTimeOffset.UtcNow.AddDays(4).AddHours(9));
        var newStart  = DateTimeOffset.UtcNow.AddDays(5).AddHours(9);

        // Crear la "nueva" cita que ya existe por idempotencia
        var existingNew = new Appointment
        {
            TenantId          = TenantId,
            PatientId         = PatientId,
            TherapistName     = "Dr. García",
            StartsAtUtc       = newStart,
            EndsAtUtc         = newStart.AddHours(1),
            Status            = AppointmentStatus.Scheduled,
            Source            = AppointmentSource.Rescheduled,
            RescheduledFromId = original.Id,
            IsRecovered       = true,
        };
        db.Appointments.Add(existingNew);
        await db.SaveChangesAsync();

        var svc = CreateService(db, CreateDuplicateIdempotency());

        var request = new RescheduleAppointmentRequest
        {
            AppointmentId  = original.Id,
            TherapistName  = "Dr. García",
            NewStartsAtUtc = newStart,
            NewEndsAtUtc   = newStart.AddHours(1),
            ActorType      = "patient",
            IdempotencyKey = "reschedule-idk-001",
        };

        // Act
        var (response, error) = await svc.RescheduleAppointmentAsync(TenantId, request);

        // Assert — devuelve la cita existente sin duplicar
        error.Should().BeNull();
        response.Should().NotBeNull();
        response!.NewAppointmentId.Should().Be(existingNew.Id);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Validators
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BookValidator_Fails_WhenStartsAtInPast()
    {
        var validator = new BookAppointmentValidator();
        var req       = new BookAppointmentRequest
        {
            PatientId     = Guid.NewGuid(),
            TherapistName = "Dr. García",
            StartsAtUtc   = DateTimeOffset.UtcNow.AddDays(-1),   // pasado
            EndsAtUtc     = DateTimeOffset.UtcNow,
        };
        var result = await validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "StartsAtUtc");
    }

    [Fact]
    public async Task BookValidator_Fails_WhenDurationOver480()
    {
        var validator = new BookAppointmentValidator();
        var start     = DateTimeOffset.UtcNow.AddDays(2);
        var req       = new BookAppointmentRequest
        {
            PatientId     = Guid.NewGuid(),
            TherapistName = "Dr. García",
            StartsAtUtc   = start,
            EndsAtUtc     = start.AddHours(9),   // 540 minutos > 480
        };
        var result = await validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CancelValidator_Fails_WhenActorTypeInvalid()
    {
        var validator = new CancelAppointmentValidator();
        var req       = new CancelAppointmentRequest
        {
            AppointmentId = Guid.NewGuid(),
            ActorType     = "hacker",
        };
        var result = await validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ActorType");
    }

    [Fact]
    public async Task GetSlotsValidator_Fails_WhenDateFromInvalid()
    {
        var validator = new GetAvailableSlotsValidator();
        var req       = new GetAvailableSlotsRequest
        {
            DateFrom = "not-a-date",
            DateTo   = "2026-06-01",
        };
        var result = await validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DateFrom");
    }

    [Fact]
    public async Task GetSlotsValidator_Fails_WhenDateToBeforeDateFrom()
    {
        var validator = new GetAvailableSlotsValidator();
        var req       = new GetAvailableSlotsRequest
        {
            DateFrom = "2026-06-10",
            DateTo   = "2026-06-05",  // anterior a DateFrom
        };
        var result = await validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DateTo");
    }
}
