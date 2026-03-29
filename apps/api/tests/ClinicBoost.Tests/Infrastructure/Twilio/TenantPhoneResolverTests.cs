using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Infrastructure.Twilio;

// ════════════════════════════════════════════════════════════════════════════
// TenantPhoneResolverTests
//
// Usa EF Core InMemory para simular la BD.
// IMemoryCache real (no mock) para verificar el comportamiento de caché.
// IServiceScopeFactory construido con ServiceCollection real.
//
// PATRÓN DE SETUP:
//   · Cada instancia de test crea su propio ServiceProvider (BD aislada).
//   · _db se obtiene desde un SCOPE explícito para evitar el anti-patrón
//     de resolver servicios Scoped desde el root provider.
//   · El TenantPhoneResolver también resuelve AppDbContext desde su propio
//     scope (vía IServiceScopeFactory), que comparte la misma BD InMemory
//     porque usa el mismo nombre.
// ════════════════════════════════════════════════════════════════════════════

public sealed class TenantPhoneResolverTests : IDisposable
{
    private readonly ServiceProvider      _rootProvider;
    private readonly IServiceScope        _dbScope;
    private readonly AppDbContext         _db;
    private readonly IMemoryCache         _cache;
    private readonly TenantPhoneResolver  _sut;

    // Únicos por instancia de test para evitar colisiones de caché entre tests
    private readonly string _clinicPhone;
    private readonly Guid   _tenantId;

    public TenantPhoneResolverTests()
    {
        // Nombre de BD único por instancia → BD totalmente aislada entre tests
        var dbName = "TenantPhoneTests_" + Guid.NewGuid().ToString("N");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseInMemoryDatabase(dbName));
        services.AddMemoryCache();
        services.AddLogging();

        _rootProvider = services.BuildServiceProvider();

        // Obtener _db desde un scope explícito para evitar el anti-patrón
        // de resolver Scoped desde el root provider (causa comportamiento
        // inesperado con EF InMemory).
        _dbScope = _rootProvider.CreateScope();
        _db      = _dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
        _cache   = _rootProvider.GetRequiredService<IMemoryCache>();

        // Usar valores únicos por instancia para evitar contaminación de caché
        _clinicPhone = "+349" + Guid.NewGuid().ToString("N")[..8];
        _tenantId    = Guid.NewGuid();

        _sut = new TenantPhoneResolver(
            _cache,
            _rootProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TenantPhoneResolver>.Instance);
    }

    public void Dispose()
    {
        _dbScope.Dispose();
        _rootProvider.Dispose();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task SeedActiveTenantAsync(string phone, Guid? id = null)
    {
        _db.Tenants.Add(new Tenant
        {
            Id             = id ?? _tenantId,
            Name           = "Clínica Test",
            Slug           = "clinica-test-" + Guid.NewGuid().ToString("N")[..6],
            TimeZone       = "Europe/Madrid",
            WhatsAppNumber = phone,
            IsActive       = true
        });
        await _db.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Resolución correcta
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveAsync_ReturnsCorrectTenantId_WhenPhoneExists()
    {
        await SeedActiveTenantAsync(_clinicPhone);

        var result = await _sut.ResolveAsync(_clinicPhone);

        result.Should().Be(_tenantId);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenPhoneNotFound()
    {
        var result = await _sut.ResolveAsync("+34999" + Guid.NewGuid().ToString("N")[..6]);

        result.Should().BeNull("número no registrado debe devolver null");
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenTenantIsInactive()
    {
        _db.Tenants.Add(new Tenant
        {
            Id             = _tenantId,
            Name           = "Inactiva",
            Slug           = "inactiva-" + Guid.NewGuid().ToString("N")[..6],
            TimeZone       = "Europe/Madrid",
            WhatsAppNumber = _clinicPhone,
            IsActive       = false   // inactivo
        });
        await _db.SaveChangesAsync();

        var result = await _sut.ResolveAsync(_clinicPhone);

        result.Should().BeNull("tenant inactivo no debe resolverse");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Comportamiento de caché
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveAsync_CachesResult_OnSecondCall()
    {
        await SeedActiveTenantAsync(_clinicPhone);

        // Primera llamada: cache miss → consulta BD
        var first = await _sut.ResolveAsync(_clinicPhone);

        // Eliminar el tenant de la BD para confirmar que la segunda llamada usa caché
        _db.Tenants.RemoveRange(_db.Tenants.ToList());
        await _db.SaveChangesAsync();

        // Segunda llamada: debe devolver el mismo resultado desde caché
        var second = await _sut.ResolveAsync(_clinicPhone);

        first.Should().Be(_tenantId);
        second.Should().Be(_tenantId,
            "la segunda llamada debe venir del caché aunque ya no exista en BD");
    }

    [Fact]
    public async Task ResolveAsync_CachesNullResult_ForUnknownPhone()
    {
        var unknownPhone = "+340" + Guid.NewGuid().ToString("N")[..8];

        // Primera llamada: null (no existe)
        var first = await _sut.ResolveAsync(unknownPhone);

        // Ahora agregar el tenant — la caché debe conservar el null (TTL corto)
        await SeedActiveTenantAsync(unknownPhone);

        // Segunda llamada: debe devolver null desde caché
        var second = await _sut.ResolveAsync(unknownPhone);

        first.Should().BeNull();
        second.Should().BeNull("el null cacheado evita hammering de BD ante números desconocidos");
    }

    [Fact]
    public async Task Invalidate_ClearsCache_SoNextCallHitsDb()
    {
        await SeedActiveTenantAsync(_clinicPhone);

        // Primer resolve: popula caché
        var first = await _sut.ResolveAsync(_clinicPhone);

        // Cambiar tenant en BD (simular cambio de número)
        var tenant = await _db.Tenants.FirstAsync();
        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync();

        var newId = Guid.NewGuid();
        await SeedActiveTenantAsync(_clinicPhone, newId);

        // Sin invalidar: devuelve el valor viejo (caché)
        var withOldCache = await _sut.ResolveAsync(_clinicPhone);

        // Invalidar caché
        _sut.Invalidate(_clinicPhone);

        // Después de invalidar: devuelve el nuevo tenant desde BD
        var afterInvalidation = await _sut.ResolveAsync(_clinicPhone);

        first.Should().Be(_tenantId);
        withOldCache.Should().Be(_tenantId, "caché conserva valor viejo sin invalidar");
        afterInvalidation.Should().Be(newId,
            "después de Invalidate() debe leer el valor nuevo de BD");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Casos límite
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_ReturnsNull_WhenPhoneIsNullOrEmpty(string? phone)
    {
        var result = await _sut.ResolveAsync(phone!);
        result.Should().BeNull("número vacío o null siempre devuelve null");
    }

    [Fact]
    public void Invalidate_DoesNotThrow_WhenPhoneNotCached()
    {
        // No debe lanzar aunque el número no esté en caché
        var act = () => _sut.Invalidate("+34000000000");
        act.Should().NotThrow();
    }

    [Fact]
    public void Invalidate_DoesNotThrow_WhenPhoneIsEmpty()
    {
        var act = () => _sut.Invalidate(string.Empty);
        act.Should().NotThrow();
    }
}
