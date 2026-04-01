using ClinicBoost.Api.Infrastructure.Tenants;
using Microsoft.AspNetCore.Mvc;

namespace ClinicBoost.Api.Features.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// ConversationEndpoints.cs
//
// Endpoints de la Inbox operacional (Vertical Slice).
//
// RUTAS
// ─────
//   GET    /api/conversations                  — lista paginada con filtros
//   GET    /api/conversations/{id}/messages    — detalle + historial
//   PATCH  /api/conversations/{id}/status      — cambio de estado operacional
//
// SEGURIDAD
// ─────────
//  · Todos los endpoints requieren JWT válido (RequireAuthorization).
//  · TenantId se extrae de ITenantContext (JWT). NUNCA del body o query.
//  · PATCH valida pertenencia al tenant antes de mutar (404 si no es del tenant).
// ════════════════════════════════════════════════════════════════════════════

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations")
            .RequireAuthorization()
            .WithTags("Conversations");

        // GET /api/conversations
        group.MapGet("/", GetInboxAsync)
            .WithSummary("Lista paginada de conversaciones con filtros operacionales.");

        // GET /api/conversations/{id}/messages
        group.MapGet("/{id:guid}/messages", GetConversationDetailAsync)
            .WithSummary("Detalle de una conversación con historial de mensajes.");

        // PATCH /api/conversations/{id}/status
        group.MapPatch("/{id:guid}/status", PatchStatusAsync)
            .WithSummary("Cambia el estado de una conversación (waiting_human / open / resolved).");

        return app;
    }

    // ── GET /api/conversations ────────────────────────────────────────────

    private static async Task<IResult> GetInboxAsync(
        [AsParameters] InboxQueryParams        q,
        IConversationInboxService              service,
        ITenantContext                          tenantCtx,
        CancellationToken                      ct)
    {
        var tenantId = tenantCtx.RequireTenantId();
        var result   = await service.GetInboxAsync(tenantId, q, ct);
        return Results.Ok(result);
    }

    // ── GET /api/conversations/{id}/messages ─────────────────────────────

    private static async Task<IResult> GetConversationDetailAsync(
        Guid                      id,
        IConversationInboxService service,
        ITenantContext             tenantCtx,
        CancellationToken         ct)
    {
        var tenantId = tenantCtx.RequireTenantId();
        var result   = await service.GetConversationDetailAsync(tenantId, id, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    // ── PATCH /api/conversations/{id}/status ─────────────────────────────

    private static async Task<IResult> PatchStatusAsync(
        Guid                           id,
        [FromBody] PatchConversationStatusRequest request,
        IConversationInboxService      service,
        ITenantContext                  tenantCtx,
        CancellationToken              ct)
    {
        if (string.IsNullOrWhiteSpace(request.Status))
            return Results.BadRequest(new { error = "El campo 'status' es obligatorio." });

        var tenantId = tenantCtx.RequireTenantId();
        var result   = await service.PatchStatusAsync(tenantId, id, request, ct);

        if (result is null)
        {
            // El servicio devuelve null en dos casos:
            //   1. Estado inválido  → 422 Unprocessable
            //   2. No encontrado    → 404 Not Found
            // Para distinguirlos revisamos si el estado enviado es válido.
            var validStatuses = new[] { "waiting_human", "open", "resolved" };
            if (!validStatuses.Contains(request.Status.ToLowerInvariant()))
                return Results.UnprocessableEntity(new
                {
                    error = $"Estado '{request.Status}' no permitido. Valores aceptados: waiting_human, open, resolved."
                });

            return Results.NotFound(new { error = $"Conversación {id} no encontrada." });
        }

        return Results.Ok(result);
    }
}
