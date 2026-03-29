using System.Net;
using System.Reflection;
using ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Tests.Infrastructure.Idempotency;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Webhooks.Voice;

// ════════════════════════════════════════════════════════════════════════════
// MissedCallEndpointTests
//
// Tests de integración del endpoint POST /webhooks/twilio/voice.
//
// ESTRATEGIA:
//   · WebApplicationFactory<Program> con overrides específicos:
//     - BD EF InMemory para evitar Postgres real
//     - ITwilioSignatureValidator: mock (controla si firma es válida)
//     - ITenantPhoneResolver: mock (controla si el tenant existe)
//     - IMissedCallJobQueue: mock (verifica que el job se encola)
//     - IIdempotencyService: implementación real sobre InMemory
//   · No necesitamos JWT porque el endpoint es AllowAnonymous.
//   · La firma Twilio la emulamos inyectando un mock del validador.
// ════════════════════════════════════════════════════════════════════════════

public sealed class MissedCallEndpointTests : IClassFixture<MissedCallWebFactory>
{
    private readonly MissedCallWebFactory _factory;

    public MissedCallEndpointTests(MissedCallWebFactory factory)
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
                     Arg.Any<string>(),
                     Arg.Any<string>())
            .Returns(false);

        var client  = _factory.CreateClient();
        var content = BuildTwilioForm("CAtest001", "+34600000001", "+34910000001");

        var response = await client.PostAsync("/webhooks/twilio/voice", content);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "firma inválida debe devolver 403");
    }

    [Fact]
    public async Task Post_Returns200TwiML_WhenSignatureIsValid()
    {
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);  // número no registrado

        var client   = _factory.CreateClient();
        var content  = BuildTwilioForm("CAtest002", "+34600000002", "+34910000002");
        var response = await client.PostAsync("/webhooks/twilio/voice", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("<Response>", "debe devolver TwiML válido");
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("text/xml");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Resolución de tenant
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenTenantNotFound_WithoutEnqueuing()
    {
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);
        _factory.JobQueue.ClearReceivedCalls();

        var client   = _factory.CreateClient();
        var content  = BuildTwilioForm("CAtest003", "+34600000003", "+34920000003");
        var response = await client.PostAsync("/webhooks/twilio/voice", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.JobQueue.DidNotReceive()
            .EnqueueAsync(Arg.Any<MissedCallJob>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Idempotencia
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenDuplicateCallSid_WithoutEnqueuing()
    {
        var callSid  = "CAdup001";
        var tenantId = Guid.NewGuid();

        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);
        _factory.JobQueue
            .EnqueueAsync(Arg.Any<MissedCallJob>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var client  = _factory.CreateClient();
        var content = BuildTwilioForm(callSid, "+34600000004", "+34910000004");

        // Primera entrega
        var first = await client.PostAsync("/webhooks/twilio/voice", content);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Contamos cuántas veces se encoló tras la primera entrega
        _factory.JobQueue.ClearReceivedCalls();

        // Segunda entrega idéntica (re-entrega de Twilio)
        var second = await client.PostAsync("/webhooks/twilio/voice",
            BuildTwilioForm(callSid, "+34600000004", "+34910000004"));

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.JobQueue.DidNotReceive()
            .EnqueueAsync(Arg.Any<MissedCallJob>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: Job encolado correctamente
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_EnqueuesJob_WithCorrectFields()
    {
        var callSid     = "CAnew001";
        var callerPhone = "+34611111111";
        var clinicPhone = "+34910000005";
        var tenantId    = Guid.NewGuid();

        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(clinicPhone, Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        MissedCallJob? capturedJob = null;
        _factory.JobQueue
            .EnqueueAsync(Arg.Do<MissedCallJob>(j => capturedJob = j),
                          Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var client  = _factory.CreateClient();
        var content = BuildTwilioForm(callSid, callerPhone, clinicPhone, "no-answer");

        var response = await client.PostAsync("/webhooks/twilio/voice", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capturedJob.Should().NotBeNull();
        capturedJob!.CallSid.Should().Be(callSid);
        capturedJob.CallerPhone.Should().Be(callerPhone);
        capturedJob.ClinicPhone.Should().Be(clinicPhone);
        capturedJob.CallStatus.Should().Be("no-answer");
        capturedJob.TenantId.Should().Be(tenantId);
        capturedJob.ProcessedEventId.Should().NotBe(Guid.Empty);
        capturedJob.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: Campos vacíos
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenCallSidIsMissing_WithoutEnqueuing()
    {
        SetupValidSignature();
        _factory.JobQueue.ClearReceivedCalls();

        var client  = _factory.CreateClient();
        // Form sin CallSid
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("From",       "+34600000009"),
            new KeyValuePair<string, string>("To",         "+34910000009"),
            new KeyValuePair<string, string>("CallStatus", "no-answer"),
        });

        var response = await client.PostAsync("/webhooks/twilio/voice", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "datos malformados deben devolver 200 para no provocar reintentos");
        await _factory.JobQueue.DidNotReceive()
            .EnqueueAsync(Arg.Any<MissedCallJob>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupValidSignature()
    {
        _factory.SignatureValidator
            .IsValid(Arg.Any<IEnumerable<KeyValuePair<string, string>>>(),
                     Arg.Any<string>(),
                     Arg.Any<string>())
            .Returns(true);
    }

    private static FormUrlEncodedContent BuildTwilioForm(
        string callSid,
        string from,
        string to,
        string status = "no-answer")
    {
        return new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("CallSid",    callSid),
            new KeyValuePair<string, string>("From",       from),
            new KeyValuePair<string, string>("To",         to),
            new KeyValuePair<string, string>("CallStatus", status),
            new KeyValuePair<string, string>("AccountSid", "ACtest"),
        });
    }
}

// ════════════════════════════════════════════════════════════════════════════
// MissedCallWebFactory
//
// WebApplicationFactory personalizada que:
//   · Reemplaza la BD por InMemory (sin Postgres real)
//   · Expone mocks de ITwilioSignatureValidator, ITenantPhoneResolver y
//     IMissedCallJobQueue para control fino desde los tests
// ════════════════════════════════════════════════════════════════════════════

public sealed class MissedCallWebFactory : WebApplicationFactory<Program>
{
    public ITwilioSignatureValidator SignatureValidator { get; } =
        Substitute.For<ITwilioSignatureValidator>();

    public ITenantPhoneResolver PhoneResolver { get; } =
        Substitute.For<ITenantPhoneResolver>();

    public IMissedCallJobQueue JobQueue { get; } =
        Substitute.For<IMissedCallJobQueue>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Proveer valores válidos de Twilio vía environment variables
        // para que ValidateOnStart() no falle antes de que
        // ConfigureServices pueda sobreescribir el IOptions<TwilioOptions>.
        builder.UseSetting("Twilio:AccountSid", "ACtest000000000000000000000000000");
        builder.UseSetting("Twilio:AuthToken",  "test_auth_token_32chars_minimum_xx");

        builder.ConfigureServices(services =>
        {
            // ── Reemplazar BD real por EF InMemory ────────────────────────────
            // AddDbContext registra 5 tipos en el IServiceCollection:
            //   · DbContextOptions                         (Scoped)
            //   · DbContextOptions<AppDbContext>           (Scoped)
            //   · IDbContextOptionsConfiguration<AppDbContext>  (Scoped) ← clave del proveedor
            //   · ServiceProviderAccessor                  (Singleton)
            //   · AppDbContext                             (Scoped)
            //
            // Si se deja IDbContextOptionsConfiguration<AppDbContext> del Npgsql y se
            // añade otro con InMemory, EF Core detecta dos proveedores en el mismo
            // IServiceProvider y lanza:
            //   "Services for database providers 'Npgsql', 'InMemory' have been registered"
            //
            // Solución: eliminar TODOS esos descriptores antes de registrar InMemory.

            // IDbContextOptionsConfiguration<AppDbContext> vive en el namespace interno
            // de EF; lo localizamos por nombre de tipo genérico.
            var efOptionsConfigType = typeof(AppDbContext).Assembly
                .GetReferencedAssemblies()
                .Select(System.Reflection.Assembly.Load)
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
                    // IDbContextOptionsConfiguration<AppDbContext> — proveedor de opciones
                    (efOptionsConfigType is not null &&
                     d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericTypeDefinition() == efOptionsConfigType &&
                     d.ServiceType.GenericTypeArguments.Length == 1 &&
                     d.ServiceType.GenericTypeArguments[0] == typeof(AppDbContext)))
                .ToList();

            foreach (var d in dbDescriptors) services.Remove(d);

            // BD aislada por instancia de factory (nombre único).
            // Sin interceptores ni proveedor Npgsql.
            var dbName = "MissedCallTests_" + Guid.NewGuid().ToString("N");
            services.AddDbContext<AppDbContext>(opts =>
                opts.UseInMemoryDatabase(dbName));

            // El TenantDbContextInterceptor debe estar en el contenedor porque
            // AddClinicBoostDatabase lo registra como Scoped y otros servicios
            // pueden pedirlo. En InMemory el guard (IsInitialized == false) lo
            // cortocircuita sin ejecutar SQL.
            services.AddScoped<TenantDbContextInterceptor>();

            // TwilioOptions: proveer valores validos para evitar ValidateOnStart
            // (los tests no usan el validator real, lo sustituimos por mock)
            services.RemoveAll<IOptions<TwilioOptions>>();
            services.RemoveAll<IOptionsSnapshot<TwilioOptions>>();
            services.RemoveAll<IOptionsMonitor<TwilioOptions>>();
            services.AddSingleton<IOptions<TwilioOptions>>(Options.Create(new TwilioOptions
            {
                AccountSid     = "ACtest000000000000000000000000000",
                AuthToken      = "test_auth_token_32chars_minimum_xx",
                WebhookBaseUrl = ""
            }));

            // Inyectar mocks
            services.RemoveAll<ITwilioSignatureValidator>();
            services.AddSingleton(SignatureValidator);

            services.RemoveAll<ITenantPhoneResolver>();
            services.AddSingleton(PhoneResolver);

            services.RemoveAll<IMissedCallJobQueue>();
            services.AddSingleton(JobQueue);

            // Idempotencia: TestableIdempotencyService usa EF puro (sin SQL crudo)
            // y es compatible con InMemory. No mezcla Npgsql + InMemory en el mismo provider.
            services.RemoveAll<IIdempotencyService>();
            services.AddScoped<IIdempotencyService, TestableIdempotencyService>();

            // Quitar el worker (no lo necesitamos en tests de endpoint)
            services.RemoveAll<MissedCallWorker>();
        });
    }
}
