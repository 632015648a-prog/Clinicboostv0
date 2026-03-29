using ClinicBoost.Api.Infrastructure.Tenants;
using FluentAssertions;

namespace ClinicBoost.Tests.Infrastructure;

/// <summary>
/// Tests de aislamiento multi-tenant.
///
/// Verifica que:
/// 1. Dos contextos de tenant distintos no se mezclan entre sí.
/// 2. RequireTenantId() devuelve SIEMPRE el tenant correcto por instancia.
/// 3. HasAtLeastRole() no usa estado de otra instancia.
/// 4. El guard de doble inicialización protege correctamente.
/// 5. Simula un escenario de N requests paralelos con tenants distintos.
/// </summary>
public sealed class MultiTenantIsolationTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (TenantContext ctxA, TenantContext ctxB) BuildTwo(
        string roleA = "admin",
        string roleB = "therapist")
    {
        var ctxA = new TenantContext();
        var ctxB = new TenantContext();

        ctxA.Initialize(Guid.NewGuid(), roleA, Guid.NewGuid());
        ctxB.Initialize(Guid.NewGuid(), roleB, Guid.NewGuid());

        return (ctxA, ctxB);
    }

    // ── Dos contextos con datos distintos no se cruzan ────────────────────────

    [Fact]
    public void TwoContexts_HaveIndependentTenantIds()
    {
        var (ctxA, ctxB) = BuildTwo();

        ctxA.TenantId!.Value.Should().NotBe(ctxB.TenantId!.Value,
            "cada request tiene su propio tenant");
        ctxA.UserId!.Value.Should().NotBe(ctxB.UserId!.Value,
            "cada request tiene su propio userId");
    }

    [Fact]
    public void TwoContexts_HaveIndependentRoles()
    {
        var (ctxA, ctxB) = BuildTwo("owner", "service");

        ctxA.UserRole.Should().Be("owner");
        ctxB.UserRole.Should().Be("service");

        ctxA.HasAtLeastRole("admin").Should().BeTrue();
        ctxB.HasAtLeastRole("admin").Should().BeFalse();
    }

    [Fact]
    public void RequireTenantId_ReturnsOwnTenant_ForEachContext()
    {
        var (ctxA, ctxB) = BuildTwo();

        var idA = ctxA.RequireTenantId();
        var idB = ctxB.RequireTenantId();

        idA.Should().Be(ctxA.TenantId!.Value);
        idB.Should().Be(ctxB.TenantId!.Value);
        idA.Should().NotBe(idB);
    }

    // ── Reinicializar un contexto lanza excepción ─────────────────────────────

    [Fact]
    public void Reinitializing_SameContext_Throws_ContextAlreadyInitialized()
    {
        var ctx = new TenantContext();
        ctx.Initialize(Guid.NewGuid(), "admin", null);

        var act = () => ctx.Initialize(Guid.NewGuid(), "owner", null);

        act.Should().Throw<TenantContextException>()
           .Where(e => e.Code == TenantContextErrorCode.ContextAlreadyInitialized);
    }

    // ── Un contexto no inicializado nunca filtra por un tenant concreto ───────

    [Fact]
    public void UninitializedContext_NeverMatchesTenantCheck()
    {
        var ctx = new TenantContext();

        ctx.IsInitialized.Should().BeFalse();
        ctx.HasAtLeastRole("service").Should().BeFalse();

        var act = () => ctx.RequireTenantId();
        act.Should().Throw<TenantContextException>()
           .Where(e => e.Code == TenantContextErrorCode.ContextNotInitialized);
    }

    // ── Simulación de N requests paralelos con tenants distintos ─────────────

    [Fact]
    public async Task ParallelRequests_WithDifferentTenants_DoNotShareState()
    {
        const int requestCount = 50;

        var tenants = Enumerable.Range(0, requestCount)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        // Simular N requests concurrentes, cada uno con su propio TenantContext (Scoped)
        var tasks = tenants.Select(async (expectedId, i) =>
        {
            // En ASP.NET Core cada request tiene su propio DI Scope
            // → su propio TenantContext instance
            var ctx = new TenantContext();
            ctx.Initialize(expectedId, i % 2 == 0 ? "admin" : "therapist", Guid.NewGuid());

            // Simular trabajo asíncrono (e.g., query a BD)
            await Task.Delay(Random.Shared.Next(1, 5));

            // Verificar que el tenant_id no se ha mezclado con otro request
            ctx.TenantId.Should().Be(expectedId,
                $"el request {i} debe ver solo su propio tenant");
            ctx.RequireTenantId().Should().Be(expectedId);

            return ctx.TenantId!.Value;
        });

        var results = await Task.WhenAll(tasks);

        // Todos los IDs son únicos (no hubo mezcla de estado)
        results.Distinct().Should().HaveCount(requestCount,
            "cada request debería tener un tenant_id único y no compartido");
    }

    // ── IsRole es case-sensitive ───────────────────────────────────────────────

    [Theory]
    [InlineData("owner",  "owner",  true)]
    [InlineData("owner",  "OWNER",  false)]   // case-sensitive: "OWNER" no es igual a "owner"
    [InlineData("admin",  "admin",  true)]
    [InlineData("admin",  "Admin",  false)]
    public void IsRole_IsCaseSensitive(string assigned, string checked_, bool expected)
    {
        var ctx = new TenantContext();
        ctx.Initialize(Guid.NewGuid(), assigned, null);

        ctx.IsRole(checked_).Should().Be(expected);
    }

    // ── Rol desconocido en Initialize se descarta → IsRole devuelve false ─────

    [Fact]
    public void Initialize_WithUnknownRole_DropsRoleAndIsRoleReturnsFalse()
    {
        var ctx = new TenantContext();
        ctx.Initialize(Guid.NewGuid(), "superadmin", null);

        ctx.UserRole.Should().BeNull();
        ctx.IsRole("superadmin").Should().BeFalse();
        ctx.HasAtLeastRole("service").Should().BeFalse();
    }

    // ── Simulación de datos cruzados: el aislamiento evita el acceso ──────────

    [Fact]
    public void CrossTenantAccess_ContextIsolationPreventsMatch()
    {
        // Tenant A y Tenant B tienen IDs distintos
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var ctxA = new TenantContext();
        ctxA.Initialize(tenantA, "admin", null);

        var ctxB = new TenantContext();
        ctxB.Initialize(tenantB, "admin", null);

        // Simular una query que intenta filtrar: "¿puede ctxA ver datos de tenantB?"
        bool canAccessTenantBData = ctxA.TenantId == tenantB;

        canAccessTenantBData.Should().BeFalse(
            "el contexto de tenant A no puede acceder a datos de tenant B");
    }

    // ── Error tipado tiene código y mensaje estructurado ─────────────────────

    [Theory]
    [InlineData(TenantContextErrorCode.ContextNotInitialized,  1005)]
    [InlineData(TenantContextErrorCode.ContextAlreadyInitialized, 1006)]
    [InlineData(TenantContextErrorCode.MissingTenantId,       1001)]
    [InlineData(TenantContextErrorCode.InvalidTenantIdFormat, 1002)]
    [InlineData(TenantContextErrorCode.InvalidRole,           1003)]
    public void TenantContextException_HasExpectedErrorCode(
        TenantContextErrorCode code, int numericCode)
    {
        var ex = new TenantContextException(code, "test message");

        ex.Code.Should().Be(code);
        ((int)ex.Code).Should().Be(numericCode);
        ex.StructuredMessage.Should().Contain(numericCode.ToString());
        ex.StructuredMessage.Should().Contain(code.ToString());
    }
}
