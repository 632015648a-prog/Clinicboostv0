using ClinicBoost.Api.Infrastructure.Tenants;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace ClinicBoost.Api.Features.Variants;

// ════════════════════════════════════════════════════════════════════════════
// VariantStatsEndpoints
//
// Endpoints REST para gestión y consulta de variantes A/B.
//
// RUTAS
// ─────
//  GET  /api/variants/{id}/stats?from=&to=
//      → Funnel completo de una variante en el rango de fechas.
//
//  GET  /api/variants/comparison?flowId=&templateId=&from=&to=
//      → Comparación de todas las variantes de un flow+template.
//        Incluye WinnerVariantKey (variante con mayor booking_rate).
//
//  GET  /api/variants?flowId=&templateId=
//      → Lista de variantes de un tenant (con IsActive y WeightPct).
//
//  POST /api/variants
//      → Crea una nueva variante.
//
//  PATCH /api/variants/{id}
//      → Actualiza WeightPct, IsActive o BodyPreview de una variante.
//
// SEGURIDAD
// ─────────
//  · Todos los endpoints requieren autenticación (JWT).
//  · El TenantId se obtiene del TenantContext (JWT claims), nunca del body.
//  · RLS activa en Postgres garantiza aislamiento multi-tenant.
//
// DISEÑO
// ──────
//  · Minimal API con MapGroup para mantener coherencia con el resto de la API.
//  · Sin dependencia directa de EF en los handlers: delega en IVariantTrackingService.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Endpoints de variantes A/B: estadísticas, comparación y gestión.
/// </summary>
public static class VariantStatsEndpoints
{
    // Regex para validar FlowId: flow_00 .. flow_07 (alineado con constraint SQL)
    private static readonly Regex FlowIdRegex = new(@"^flow_0[0-7]$", RegexOptions.Compiled);
    // Rango máximo permitido en queries de stats para proteger rendimiento
    private const int MaxRangeDays = 365;

    public static IEndpointRouteBuilder MapVariantEndpoints(
        this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/variants")
            .RequireAuthorization()
            .WithTags("Variants A/B");

        // ── GET /api/variants/{id}/stats ──────────────────────────────────────
        group.MapGet("/{id:guid}/stats", async (
            Guid                     id,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            IVariantTrackingService  svc,
            ITenantContext           tenantCtx) =>
        {
            var rangeFrom = from ?? DateTimeOffset.UtcNow.AddDays(-30);
            var rangeTo   = to   ?? DateTimeOffset.UtcNow;

            if (rangeFrom >= rangeTo)
                return Results.BadRequest("'from' debe ser anterior a 'to'.");

            // P1: limitar rango máximo para proteger rendimiento
            if ((rangeTo - rangeFrom).TotalDays > MaxRangeDays)
                return Results.BadRequest($"El rango máximo permitido es {MaxRangeDays} días.");

            var stats = await svc.GetVariantStatsAsync(
                tenantCtx.TenantId, id, rangeFrom, rangeTo);

            if (stats.SentCount == 0 && stats.VariantId == Guid.Empty)
                return Results.NotFound($"Variante {id} no encontrada para este tenant.");

            return Results.Ok(new VariantStatsResponse { Stats = stats });
        })
        .WithName("GetVariantStats")
        .WithSummary("Funnel de conversión de una variante A/B")
        .Produces<VariantStatsResponse>()
        .Produces(StatusCodes.Status404NotFound);

        // ── GET /api/variants/comparison ──────────────────────────────────────
        group.MapGet("/comparison", async (
            [FromQuery] string           flowId,
            [FromQuery] string?          templateId,
            [FromQuery] DateTimeOffset?  from,
            [FromQuery] DateTimeOffset?  to,
            IVariantTrackingService      svc,
            ITenantContext               tenantCtx) =>
        {
            if (string.IsNullOrWhiteSpace(flowId))
                return Results.BadRequest("'flowId' es obligatorio.");

            // P1: validar formato flowId con regex (flow_00..flow_07)
            if (!FlowIdRegex.IsMatch(flowId))
                return Results.BadRequest("'flowId' debe tener formato 'flow_0N' (N=0-7).");

            var rangeFrom = from ?? DateTimeOffset.UtcNow.AddDays(-30);
            var rangeTo   = to   ?? DateTimeOffset.UtcNow;

            if (rangeFrom >= rangeTo)
                return Results.BadRequest("'from' debe ser anterior a 'to'.");

            // P1: limitar rango máximo
            if ((rangeTo - rangeFrom).TotalDays > MaxRangeDays)
                return Results.BadRequest($"El rango máximo permitido es {MaxRangeDays} días.");

            var variants = await svc.GetVariantComparisonAsync(
                tenantCtx.TenantId, flowId, templateId, rangeFrom, rangeTo);

            // Determinar variante ganadora (mayor booking_rate con al menos 10 enviados)
            var winner = variants
                .Where(v => v.SentCount >= 10)
                .OrderByDescending(v => v.BookingRate)
                .FirstOrDefault();

            return Results.Ok(new VariantComparisonResponse
            {
                FlowId           = flowId,
                TemplateId       = templateId,
                From             = rangeFrom,
                To               = rangeTo,
                Variants         = variants,
                WinnerVariantKey = winner?.VariantKey,
            });
        })
        .WithName("GetVariantComparison")
        .WithSummary("Comparación del funnel de conversión entre variantes A/B")
        .Produces<VariantComparisonResponse>();

        // ── GET /api/variants ─────────────────────────────────────────────────
        group.MapGet("/", async (
            [FromQuery] string?          flowId,
            [FromQuery] string?          templateId,
            ClinicBoost.Api.Infrastructure.Database.AppDbContext db,
            ITenantContext               tenantCtx) =>
        {
            var query = db.MessageVariants
                .Where(v => v.TenantId == tenantCtx.TenantId);

            if (!string.IsNullOrEmpty(flowId))
                query = query.Where(v => v.FlowId == flowId);

            if (!string.IsNullOrEmpty(templateId))
                query = query.Where(v => v.TemplateId == templateId);

            var variants = await query
                .OrderBy(v => v.FlowId)
                .ThenBy(v => v.TemplateId)
                .ThenBy(v => v.VariantKey)
                .Select(v => new
                {
                    v.Id,
                    v.FlowId,
                    v.TemplateId,
                    v.VariantKey,
                    v.BodyPreview,
                    v.WeightPct,
                    v.IsActive,
                    v.CreatedAt,
                    v.UpdatedAt,
                })
                .ToListAsync();

            return Results.Ok(variants);
        })
        .WithName("ListVariants")
        .WithSummary("Lista variantes A/B del tenant");

        // ── POST /api/variants ────────────────────────────────────────────────
        group.MapPost("/", async (
            [FromBody] CreateVariantRequest           req,
            ClinicBoost.Api.Infrastructure.Database.AppDbContext db,
            ITenantContext                            tenantCtx) =>
        {
            if (string.IsNullOrWhiteSpace(req.FlowId))
                return Results.BadRequest("'flowId' es obligatorio.");
            // P1: validar formato flowId con regex
            if (!FlowIdRegex.IsMatch(req.FlowId))
                return Results.BadRequest("'flowId' debe tener formato 'flow_0N' (N=0-7).");
            if (string.IsNullOrWhiteSpace(req.TemplateId))
                return Results.BadRequest("'templateId' es obligatorio.");
            if (string.IsNullOrWhiteSpace(req.VariantKey))
                return Results.BadRequest("'variantKey' es obligatorio.");
            // P1: limitar longitud de variantKey alineado con constraint SQL (32 chars)
            if (req.VariantKey.Length > 32)
                return Results.BadRequest("'variantKey' no puede superar 32 caracteres.");
            if (req.WeightPct < 0 || req.WeightPct > 100)
                return Results.BadRequest("'weightPct' debe estar entre 0 y 100.");

            // Verificar unicidad dentro del tenant
            var exists = await db.MessageVariants.AnyAsync(v =>
                v.TenantId   == tenantCtx.TenantId &&
                v.FlowId     == req.FlowId         &&
                v.TemplateId == req.TemplateId     &&
                v.VariantKey == req.VariantKey);

            if (exists)
                return Results.Conflict(
                    $"Ya existe una variante '{req.VariantKey}' para {req.FlowId}/{req.TemplateId}.");

            var variant = new ClinicBoost.Domain.Variants.MessageVariant
            {
                TenantId     = tenantCtx.TenantId,
                FlowId       = req.FlowId,
                TemplateId   = req.TemplateId,
                VariantKey   = req.VariantKey,
                BodyPreview  = req.BodyPreview,
                TemplateVars = req.TemplateVars,
                WeightPct    = req.WeightPct,
                IsActive     = req.IsActive,
                Metadata     = req.Metadata,
            };

            db.MessageVariants.Add(variant);
            await db.SaveChangesAsync();

            return Results.Created($"/api/variants/{variant.Id}", new { variant.Id, variant.VariantKey });
        })
        .WithName("CreateVariant")
        .WithSummary("Crea una nueva variante A/B")
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status409Conflict);

        // ── PATCH /api/variants/{id} ──────────────────────────────────────────
        group.MapPatch("/{id:guid}", async (
            Guid                         id,
            [FromBody] UpdateVariantRequest req,
            ClinicBoost.Api.Infrastructure.Database.AppDbContext db,
            ITenantContext               tenantCtx) =>
        {
            var variant = await db.MessageVariants
                .FirstOrDefaultAsync(v =>
                    v.Id       == id              &&
                    v.TenantId == tenantCtx.TenantId);

            if (variant is null)
                return Results.NotFound($"Variante {id} no encontrada.");

            if (req.WeightPct.HasValue)
            {
                if (req.WeightPct.Value < 0 || req.WeightPct.Value > 100)
                    return Results.BadRequest("'weightPct' debe estar entre 0 y 100.");
                variant.WeightPct = req.WeightPct.Value;
            }

            if (req.IsActive.HasValue)
                variant.IsActive = req.IsActive.Value;

            if (req.BodyPreview is not null)
                variant.BodyPreview = req.BodyPreview;

            if (req.Metadata is not null)
                variant.Metadata = req.Metadata;

            variant.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                variant.Id,
                variant.VariantKey,
                variant.WeightPct,
                variant.IsActive,
                variant.UpdatedAt,
            });
        })
        .WithName("UpdateVariant")
        .WithSummary("Actualiza peso, estado o preview de una variante A/B")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        return routes;
    }
}

// ── Alias interno para evitar FQDN repetitivo ─────────────────────────────────
file static class DbExtensions
{
    public static System.Threading.Tasks.Task<bool> AnyAsync<T>(
        this Microsoft.EntityFrameworkCore.DbSet<T> set,
        System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        where T : class
        => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .AnyAsync(set, predicate);

    public static System.Threading.Tasks.Task<T?> FirstOrDefaultAsync<T>(
        this Microsoft.EntityFrameworkCore.DbSet<T> set,
        System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        where T : class
        => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(set, predicate);

    public static System.Threading.Tasks.Task<System.Collections.Generic.List<TResult>> ToListAsync<T, TResult>(
        this System.Linq.IQueryable<TResult> query)
        => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(query);
}
