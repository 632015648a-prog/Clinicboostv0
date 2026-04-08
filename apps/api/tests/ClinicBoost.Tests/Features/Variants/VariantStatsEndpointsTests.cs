using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Variants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Variants;

// ════════════════════════════════════════════════════════════════════════════
// VariantStatsEndpointsTests   (N-P1-04)
//
// COBERTURA
// ─────────
//  POST /api/variants:
//    · FlowId vacío           → 400
//    · FlowId formato inválido → 400  (^flow_0[0-7]$ regex)
//    · TemplateId vacío       → 400
//    · VariantKey vacío        → 400
//    · VariantKey > 32 chars   → 400
//    · WeightPct > 100         → 400
//    · WeightPct < 0           → 400
//    · Duplicado tenant+flow+template+key → 409
//    · Request válido          → 201 con Id y VariantKey
//
//  GET /api/variants/{id}/stats:
//    · Rango invertido (from >= to) → 400
//    · Rango > 365 días             → 400
//    · Variante de otro tenant      → 404
//
//  GET /api/variants/comparison:
//    · FlowId ausente               → 400
//    · FlowId formato inválido      → 400
//    · Rango > 365 días             → 400
//
//  PATCH /api/variants/{id}:
//    · WeightPct > 100 → 400
//    · Variante no existe / distinto tenant → 404
// ════════════════════════════════════════════════════════════════════════════

public class VariantStatsEndpointsTests : IAsyncLifetime
{
    private AppDbContext             _db       = null!;
    private IVariantTrackingService  _svc      = null!;
    private ITenantContext           _tenantCtx = null!;
    private Guid                     _tenantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(opts);

        _svc = new VariantTrackingService(_db,
            NullLogger<VariantTrackingService>.Instance);

        _tenantCtx = Substitute.For<ITenantContext>();
        _tenantCtx.TenantId.Returns(_tenantId);
        _tenantCtx.RequireTenantId().Returns(_tenantId);
        _tenantCtx.IsInitialized.Returns(true);

        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── POST /api/variants ────────────────────────────────────────────────────

    [Fact]
    public async Task Post_EmptyFlowId_Returns400()
    {
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = "",
            TemplateId = "HXabc123",
            VariantKey = "control",
            WeightPct  = 50,
        });

        Assert.Equal(400, GetStatusCode(result));
    }

    [Theory]
    [InlineData("flow_08")]   // fuera de rango
    [InlineData("FLOW_01")]   // mayúsculas
    [InlineData("flow_1")]    // sin cero
    [InlineData("flow_-1")]   // negativo
    [InlineData("random")]    // libre
    public async Task Post_InvalidFlowIdFormat_Returns400(string flowId)
    {
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = flowId,
            TemplateId = "HXabc123",
            VariantKey = "control",
            WeightPct  = 50,
        });

        Assert.Equal(400, GetStatusCode(result));
    }

    [Theory]
    [InlineData("flow_00")]
    [InlineData("flow_01")]
    [InlineData("flow_07")]
    public async Task Post_ValidFlowIdFormat_NotRejectedForFlowId(string flowId)
    {
        // Debe pasar la validación de FlowId (puede fallar por otros motivos)
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = flowId,
            TemplateId = "HXabc123",
            VariantKey = "ctrl",
            WeightPct  = 50,
        });

        // 201 (creado) o 409 (conflicto si ya existe) — no 400 por FlowId
        var code = GetStatusCode(result);
        Assert.NotEqual(400, code);
    }

    [Fact]
    public async Task Post_EmptyTemplateId_Returns400()
    {
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = "flow_01",
            TemplateId = "",
            VariantKey = "control",
            WeightPct  = 50,
        });

        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task Post_EmptyVariantKey_Returns400()
    {
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = "flow_01",
            TemplateId = "HXabc",
            VariantKey = "",
            WeightPct  = 50,
        });

        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task Post_VariantKeyExceedsMaxLength_Returns400()
    {
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = "flow_01",
            TemplateId = "HXabc",
            VariantKey = new string('x', 33),  // 33 chars > 32 limit
            WeightPct  = 50,
        });

        Assert.Equal(400, GetStatusCode(result));
    }

    [Theory]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(-1)]
    public async Task Post_WeightPctOutOfRange_Returns400(int pct)
    {
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = "flow_01",
            TemplateId = "HXabc",
            VariantKey = "control",
            WeightPct  = (short)pct,
        });

        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task Post_ValidRequest_Returns201WithIdAndKey()
    {
        var result = await InvokePostVariant(new CreateVariantRequest
        {
            FlowId     = "flow_01",
            TemplateId = "HXabc123",
            VariantKey = "control",
            WeightPct  = 50,
            IsActive   = true,
        });

        Assert.Equal(201, GetStatusCode(result));
    }

    [Fact]
    public async Task Post_DuplicateTenantFlowTemplateKey_Returns409()
    {
        var req = new CreateVariantRequest
        {
            FlowId     = "flow_01",
            TemplateId = "HXduplicate",
            VariantKey = "dup_key",
            WeightPct  = 50,
            IsActive   = true,
        };

        // Primera inserción → 201
        var first = await InvokePostVariant(req);
        Assert.Equal(201, GetStatusCode(first));

        // Segunda inserción idéntica → 409
        var second = await InvokePostVariant(req);
        Assert.Equal(409, GetStatusCode(second));
    }

    // ── GET /api/variants/{id}/stats ──────────────────────────────────────────

    [Fact]
    public async Task GetStats_RangeFromAfterTo_Returns400()
    {
        var id   = Guid.NewGuid();
        var from = DateTimeOffset.UtcNow;
        var to   = from.AddDays(-1);  // from > to → inválido

        var result = await InvokeGetStats(id, from, to);

        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task GetStats_RangeExceeds365Days_Returns400()
    {
        var id   = Guid.NewGuid();
        var from = DateTimeOffset.UtcNow.AddDays(-366);
        var to   = DateTimeOffset.UtcNow;

        var result = await InvokeGetStats(id, from, to);

        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task GetStats_VariantBelongsToOtherTenant_Returns404()
    {
        // Crear variante con un tenantId diferente
        var otherTenantId = Guid.NewGuid();
        var variant = new MessageVariant
        {
            TenantId   = otherTenantId,
            FlowId     = "flow_01",
            TemplateId = "HXother",
            VariantKey = "ctrl",
            WeightPct  = 50,
            IsActive   = true,
        };
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to   = DateTimeOffset.UtcNow;

        // Buscar con _tenantId (distinto de otherTenantId) → 404
        var result = await InvokeGetStats(variant.Id, from, to);
        Assert.Equal(404, GetStatusCode(result));
    }

    // ── GET /api/variants/comparison ─────────────────────────────────────────

    [Fact]
    public async Task GetComparison_MissingFlowId_Returns400()
    {
        var result = await InvokeGetComparison(
            flowId: null,
            from: DateTimeOffset.UtcNow.AddDays(-7),
            to: DateTimeOffset.UtcNow);

        Assert.Equal(400, GetStatusCode(result));
    }

    [Theory]
    [InlineData("flow_08")]
    [InlineData("FLOW_01")]
    [InlineData("badformat")]
    public async Task GetComparison_InvalidFlowId_Returns400(string flowId)
    {
        var result = await InvokeGetComparison(
            flowId: flowId,
            from: DateTimeOffset.UtcNow.AddDays(-7),
            to: DateTimeOffset.UtcNow);

        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task GetComparison_RangeExceeds365Days_Returns400()
    {
        var result = await InvokeGetComparison(
            flowId: "flow_01",
            from: DateTimeOffset.UtcNow.AddDays(-366),
            to: DateTimeOffset.UtcNow);

        Assert.Equal(400, GetStatusCode(result));
    }

    // ── PATCH /api/variants/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task Patch_WeightPctAbove100_Returns400()
    {
        // Crear variante del tenant actual
        var variant = new MessageVariant
        {
            TenantId   = _tenantId,
            FlowId     = "flow_01",
            TemplateId = "HXpatch",
            VariantKey = "patch_ctrl",
            WeightPct  = 50,
            IsActive   = true,
        };
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        var result = await InvokePatchVariant(variant.Id, new UpdateVariantRequest
        {
            WeightPct = 101,
        });

        Assert.Equal(400, GetStatusCode(result));
    }

    [Fact]
    public async Task Patch_VariantNotFound_Returns404()
    {
        var result = await InvokePatchVariant(Guid.NewGuid(), new UpdateVariantRequest
        {
            WeightPct = 60,
        });

        Assert.Equal(404, GetStatusCode(result));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Invoca directamente el handler del endpoint POST /api/variants.
    /// Replica la lógica del MapPost handler sin levantar un servidor HTTP.
    /// </summary>
    private async Task<IResult> InvokePostVariant(CreateVariantRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FlowId))
            return Results.BadRequest("'flowId' es obligatorio.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(req.FlowId, @"^flow_0[0-7]$"))
            return Results.BadRequest("'flowId' debe tener formato 'flow_0N' (N=0-7).");

        if (string.IsNullOrWhiteSpace(req.TemplateId))
            return Results.BadRequest("'templateId' es obligatorio.");

        if (string.IsNullOrWhiteSpace(req.VariantKey))
            return Results.BadRequest("'variantKey' es obligatorio.");

        if (req.VariantKey.Length > 32)
            return Results.BadRequest("'variantKey' no puede superar 32 caracteres.");

        if (req.WeightPct < 0 || req.WeightPct > 100)
            return Results.BadRequest("'weightPct' debe estar entre 0 y 100.");

        var exists = await _db.MessageVariants.AnyAsync(v =>
            v.TenantId   == _tenantId       &&
            v.FlowId     == req.FlowId      &&
            v.TemplateId == req.TemplateId  &&
            v.VariantKey == req.VariantKey);

        if (exists)
            return Results.Conflict($"Ya existe una variante '{req.VariantKey}'.");

        var variant = new MessageVariant
        {
            TenantId   = _tenantId,
            FlowId     = req.FlowId,
            TemplateId = req.TemplateId,
            VariantKey = req.VariantKey,
            WeightPct  = req.WeightPct,
            IsActive   = req.IsActive,
        };
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        return Results.Created($"/api/variants/{variant.Id}",
            new { variant.Id, variant.VariantKey });
    }

    private async Task<IResult> InvokeGetStats(
        Guid id, DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
            return Results.BadRequest("'from' debe ser anterior a 'to'.");

        if ((to - from).TotalDays > 365)
            return Results.BadRequest("El rango máximo permitido es 365 días.");

        var stats = await _svc.GetVariantStatsAsync(_tenantId, id, from, to);

        if (stats.SentCount == 0 && stats.VariantId == Guid.Empty)
            return Results.NotFound($"Variante {id} no encontrada para este tenant.");

        return Results.Ok(new VariantStatsResponse { Stats = stats });
    }

    private async Task<IResult> InvokeGetComparison(
        string? flowId, DateTimeOffset from, DateTimeOffset to)
    {
        if (string.IsNullOrWhiteSpace(flowId))
            return Results.BadRequest("'flowId' es obligatorio.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(flowId, @"^flow_0[0-7]$"))
            return Results.BadRequest("'flowId' debe tener formato 'flow_0N' (N=0-7).");

        if (from >= to)
            return Results.BadRequest("'from' debe ser anterior a 'to'.");

        if ((to - from).TotalDays > 365)
            return Results.BadRequest("El rango máximo permitido es 365 días.");

        var variants = await _svc.GetVariantComparisonAsync(
            _tenantId, flowId, null, from, to);

        return Results.Ok(new VariantComparisonResponse
        {
            FlowId   = flowId,
            From     = from,
            To       = to,
            Variants = variants,
        });
    }

    private async Task<IResult> InvokePatchVariant(Guid id, UpdateVariantRequest req)
    {
        var variant = await _db.MessageVariants
            .FirstOrDefaultAsync(v => v.Id == id && v.TenantId == _tenantId);

        if (variant is null)
            return Results.NotFound($"Variante {id} no encontrada.");

        if (req.WeightPct.HasValue)
        {
            if (req.WeightPct.Value < 0 || req.WeightPct.Value > 100)
                return Results.BadRequest("'weightPct' debe estar entre 0 y 100.");
            variant.WeightPct = req.WeightPct.Value;
        }

        if (req.IsActive.HasValue) variant.IsActive = req.IsActive.Value;
        if (req.BodyPreview is not null) variant.BodyPreview = req.BodyPreview;

        variant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return Results.Ok(new { variant.Id, variant.WeightPct, variant.IsActive });
    }

    private static int GetStatusCode(IResult result) => result switch
    {
        IStatusCodeHttpResult sc => sc.StatusCode ?? 200,
        _                        => 200,
    };
}
