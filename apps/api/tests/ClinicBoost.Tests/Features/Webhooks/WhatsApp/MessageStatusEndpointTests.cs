using System.Net;
using System.Reflection;
using ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Tests.Infrastructure.Idempotency;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Webhooks.WhatsApp;

// ════════════════════════════════════════════════════════════════════════════
// MessageStatusEndpointTests
//
// Tests de integración de POST /webhooks/twilio/message-status.
//
// ESTRATEGIA:
//   · WebApplicationFactory<Program> con BD InMemory, mocks de firma/tenant.
//   · IMessageStatusService REAL: usamos la implementación concreta sobre
//     InMemory para verificar que Message y MessageDeliveryEvent se persisten.
//   · IIdempotencyService: TestableIdempotencyService (EF puro, sin SQL crudo).
// ════════════════════════════════════════════════════════════════════════════

public sealed class MessageStatusEndpointTests
    : IClassFixture<MessageStatusWebFactory>
{
    private readonly MessageStatusWebFactory _factory;

    public MessageStatusEndpointTests(MessageStatusWebFactory factory)
    {
        _factory = factory;
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Validación de firma
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns403_WhenSignatureIsInvalid()
    {
        _factory.SignatureValidator
            .IsValid(Arg.Any<IEnumerable<KeyValuePair<string, string>>>(),
                     Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        var response = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm("SM001", "delivered", "+34910000001"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Resolución de tenant
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenTenantNotFound()
    {
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var response = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm("SM002", "delivered", "+34900000000"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Idempotencia
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenDuplicateCallback_WithoutDoubleInsert()
    {
        var messageSid = "SMidem001";
        var tenantId   = Guid.NewGuid();
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        var form = BuildStatusForm(messageSid, "delivered", "+34910000002");

        // Primera entrega
        var r1 = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status", form);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);

        // Segunda entrega idéntica
        var r2 = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm(messageSid, "delivered", "+34910000002"));
        r2.StatusCode.Should().Be(HttpStatusCode.OK);

        // Solo un MessageDeliveryEvent debe haberse insertado
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.MessageDeliveryEvents
            .CountAsync(e => e.TenantId == tenantId &&
                             e.ProviderMessageId == messageSid &&
                             e.Status == "delivered");

        count.Should().Be(1, "el duplicado no debe crear un segundo evento");
    }

    [Fact]
    public async Task Post_Returns200_ForDifferentStatuses_SameMessageSid()
    {
        // sent y delivered del mismo SID son callbacks distintos → dos registros
        var messageSid = "SMmulti001";
        var tenantId   = Guid.NewGuid();
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm(messageSid, "sent", "+34910000003"));

        await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm(messageSid, "delivered", "+34910000003"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = await db.MessageDeliveryEvents
            .Where(e => e.TenantId == tenantId &&
                        e.ProviderMessageId == messageSid)
            .ToListAsync();

        events.Should().HaveCount(2,
            "sent y delivered son transiciones distintas → dos filas");
        events.Select(e => e.Status).Should()
            .BeEquivalentTo(["sent", "delivered"]);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: Persistencia de MessageDeliveryEvent
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_PersistsDeliveryEvent_WithCorrectFields_OnDelivered()
    {
        var messageSid = "SMdelivered001";
        var tenantId   = Guid.NewGuid();
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        var response = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm(messageSid, "delivered", "+34910000004",
                    toNumber: "whatsapp:+34612000001"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var evt = await db.MessageDeliveryEvents
            .FirstOrDefaultAsync(e => e.TenantId == tenantId &&
                                      e.ProviderMessageId == messageSid);

        evt.Should().NotBeNull();
        evt!.Status.Should().Be("delivered");
        evt.Channel.Should().Be("whatsapp",
            "el From lleva prefijo 'whatsapp:' → canal whatsapp");
        evt.TenantId.Should().Be(tenantId);
        evt.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow,
            precision: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Post_PersistsDeliveryEvent_WithErrorFields_OnFailed()
    {
        var messageSid = "SMfailed001";
        var tenantId   = Guid.NewGuid();
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        var response = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm(messageSid, "failed", "+34910000005",
                    errorCode: "30008", errorMessage: "Unknown destination handset"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var evt = await db.MessageDeliveryEvents
            .FirstOrDefaultAsync(e => e.TenantId == tenantId &&
                                      e.ProviderMessageId == messageSid);

        evt.Should().NotBeNull();
        evt!.Status.Should().Be("failed");
        evt.ErrorCode.Should().Be("30008");
        evt.ErrorMessage.Should().Be("Unknown destination handset");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: Actualización de Message.Status cuando el Message existe
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_UpdatesMessageStatus_WhenMessageExistsInDb()
    {
        var messageSid = "SMupdate001";
        var tenantId   = Guid.NewGuid();
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        // Pre-insertar un Message con status "sent" en la BD de test
        Guid messageId;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var msg = new Message
            {
                TenantId          = tenantId,
                ConversationId    = Guid.NewGuid(),
                Direction         = "outbound",
                Channel           = "whatsapp",
                ProviderMessageId = messageSid,
                Body              = "Hola, te llamamos",
                Status            = "sent",
            };
            db.Messages.Add(msg);
            await db.SaveChangesAsync();
            messageId = msg.Id;
        }

        // Enviar callback "delivered"
        var response = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm(messageSid, "delivered", "+34910000006"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verificar que el Message fue actualizado
        using var scope = _factory.Services.CreateScope();
        var dbCheck = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var updatedMsg = await dbCheck.Messages.FindAsync(messageId);
        updatedMsg!.Status.Should().Be("delivered",
            "el callback 'delivered' debe actualizar Message.Status");
        updatedMsg.DeliveredAt.Should().NotBeNull(
            "DeliveredAt debe establecerse en la transición 'delivered'");

        var deliveryEvt = await dbCheck.MessageDeliveryEvents
            .FirstOrDefaultAsync(e => e.ProviderMessageId == messageSid &&
                                      e.Status == "delivered");
        deliveryEvt.Should().NotBeNull();
        deliveryEvt!.MessageId.Should().Be(messageId,
            "el MessageDeliveryEvent debe correlacionarse con el Message por Id");
    }

    [Fact]
    public async Task Post_UpdatesMessageErrorFields_OnFailed()
    {
        var messageSid = "SMfail002";
        var tenantId   = Guid.NewGuid();
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        Guid messageId;
        using (var setupScope = _factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var msg = new Message
            {
                TenantId          = tenantId,
                ConversationId    = Guid.NewGuid(),
                Direction         = "outbound",
                Channel           = "whatsapp",
                ProviderMessageId = messageSid,
                Body              = "Mensaje de prueba",
                Status            = "sent",
            };
            db.Messages.Add(msg);
            await db.SaveChangesAsync();
            messageId = msg.Id;
        }

        await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status",
                BuildStatusForm(messageSid, "failed", "+34910000007",
                    errorCode: "30006", errorMessage: "Landline or unreachable"));

        using var scope = _factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var msg2 = await db2.Messages.FindAsync(messageId);
        msg2!.Status.Should().Be("failed");
        msg2.ErrorCode.Should().Be("30006");
        msg2.ErrorMessage.Should().Be("Landline or unreachable");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 6: Campos vacíos / malformados
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenMessageSidIsMissing()
    {
        SetupValidSignature();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("MessageStatus", "delivered"),
            new KeyValuePair<string, string>("From", "whatsapp:+34910000008"),
            new KeyValuePair<string, string>("AccountSid", "ACtest"),
        });

        var response = await _factory.CreateClient()
            .PostAsync("/webhooks/twilio/message-status", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "campos vacíos deben devolver 200 para no provocar reintentos");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupValidSignature() =>
        _factory.SignatureValidator
            .IsValid(Arg.Any<IEnumerable<KeyValuePair<string, string>>>(),
                     Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

    /// <summary>
    /// Construye un form de callback de estado de mensaje de Twilio.
    /// fromNumber = número de la clínica (origen del mensaje outbound).
    /// toNumber   = número del paciente (destino).
    /// </summary>
    private static FormUrlEncodedContent BuildStatusForm(
        string  messageSid,
        string  status,
        string  fromNumber,
        string? toNumber     = null,
        string? errorCode    = null,
        string? errorMessage = null)
    {
        var from = fromNumber.StartsWith("whatsapp:") ? fromNumber : $"whatsapp:{fromNumber}";
        var to   = toNumber ?? "whatsapp:+34612000001";

        var pairs = new List<KeyValuePair<string, string>>
        {
            new("MessageSid",    messageSid),
            new("MessageStatus", status),
            new("From",          from),
            new("To",            to),
            new("AccountSid",    "ACtest"),
        };

        if (errorCode    is not null) pairs.Add(new("ErrorCode",    errorCode));
        if (errorMessage is not null) pairs.Add(new("ErrorMessage", errorMessage));

        return new FormUrlEncodedContent(pairs);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// MessageStatusWebFactory
// ════════════════════════════════════════════════════════════════════════════

public sealed class MessageStatusWebFactory : WebApplicationFactory<Program>
{
    public ITwilioSignatureValidator SignatureValidator { get; } =
        Substitute.For<ITwilioSignatureValidator>();

    public ITenantPhoneResolver PhoneResolver { get; } =
        Substitute.For<ITenantPhoneResolver>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Twilio:AccountSid", "ACtest000000000000000000000000000");
        builder.UseSetting("Twilio:AuthToken",  "test_auth_token_32chars_minimum_xx");

        builder.ConfigureServices(services =>
        {
            // ── Reemplazar BD real por EF InMemory ────────────────────────────
            var efOptionsConfigType = typeof(AppDbContext).Assembly
                .GetReferencedAssemblies()
                .Select(Assembly.Load)
                .Concat([typeof(AppDbContext).Assembly])
                .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                .FirstOrDefault(t =>
                    t.IsGenericTypeDefinition &&
                    t.Name == "IDbContextOptionsConfiguration`1");

            var typesToRemove = new HashSet<Type>
            {
                typeof(DbContextOptions<AppDbContext>),
                typeof(DbContextOptions),
                typeof(AppDbContext),
                typeof(TenantDbContextInterceptor),
            };

            var dbDescriptors = services
                .Where(d =>
                    typesToRemove.Contains(d.ServiceType) ||
                    (efOptionsConfigType is not null &&
                     d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericTypeDefinition() == efOptionsConfigType &&
                     d.ServiceType.GenericTypeArguments.Length == 1 &&
                     d.ServiceType.GenericTypeArguments[0] == typeof(AppDbContext)))
                .ToList();

            foreach (var d in dbDescriptors) services.Remove(d);

            // BD única por factory (mismo nombre para que el test pueda leer
            // lo que el endpoint escribió en el mismo DbContext InMemory store)
            var dbName = "MsgStatusTests_" + Guid.NewGuid().ToString("N");
            services.AddDbContext<AppDbContext>(opts =>
                opts.UseInMemoryDatabase(dbName));

            services.AddScoped<TenantDbContextInterceptor>();

            // TwilioOptions
            services.RemoveAll<IOptions<TwilioOptions>>();
            services.RemoveAll<IOptionsSnapshot<TwilioOptions>>();
            services.RemoveAll<IOptionsMonitor<TwilioOptions>>();
            services.AddSingleton<IOptions<TwilioOptions>>(Options.Create(new TwilioOptions
            {
                AccountSid     = "ACtest000000000000000000000000000",
                AuthToken      = "test_auth_token_32chars_minimum_xx",
                WebhookBaseUrl = ""
            }));

            // Mocks de firma y tenant
            services.RemoveAll<ITwilioSignatureValidator>();
            services.AddSingleton(SignatureValidator);

            services.RemoveAll<ITenantPhoneResolver>();
            services.AddSingleton(PhoneResolver);

            // Idempotencia compatible con InMemory
            services.RemoveAll<IIdempotencyService>();
            services.AddScoped<IIdempotencyService, TestableIdempotencyService>();

            // IMessageStatusService REAL (no mock) para verificar persistencia
            // Ya registrado por AddMessageStatusFeature(); no necesitamos reemplazarlo.
        });
    }
}
