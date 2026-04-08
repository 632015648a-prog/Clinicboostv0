namespace ClinicBoost.Api.Features.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// IConversationInboxService.cs
//
// Contrato del servicio de la Inbox operacional.
// Separa las responsabilidades de la Inbox de las del pipeline de inbound
// (IConversationService en Webhooks.WhatsApp.Inbound).
// ════════════════════════════════════════════════════════════════════════════

public interface IConversationInboxService
{
    /// <summary>
    /// Lista paginada de conversaciones con filtros operacionales.
    /// Siempre filtra por tenantId.
    /// </summary>
    Task<InboxListResponse> GetInboxAsync(
        Guid              tenantId,
        InboxQueryParams  query,
        CancellationToken ct = default);

    /// <summary>
    /// Detalle de una conversación: cabecera + historial de mensajes.
    /// Devuelve null si la conversación no pertenece al tenant.
    /// </summary>
    Task<ConversationDetailResponse?> GetConversationDetailAsync(
        Guid              tenantId,
        Guid              conversationId,
        CancellationToken ct = default);

    /// <summary>
    /// Cambia el estado de una conversación (handoff humano / reactivar / resolver).
    /// Devuelve null si la conversación no pertenece al tenant (404 en endpoint).
    /// </summary>
    Task<PatchConversationStatusResponse?> PatchStatusAsync(
        Guid                           tenantId,
        Guid                           conversationId,
        PatchConversationStatusRequest request,
        CancellationToken              ct = default);

    /// <summary>
    /// Resumen ligero de conversaciones en estado waiting_human.
    /// Devuelve el total y las 10 más antiguas (mayor urgencia primero).
    /// Diseñado para polling corto (~30 s) desde el widget del dashboard.
    /// </summary>
    Task<PendingHandoffResponse> GetPendingHandoffAsync(
        Guid              tenantId,
        CancellationToken ct = default);
}
