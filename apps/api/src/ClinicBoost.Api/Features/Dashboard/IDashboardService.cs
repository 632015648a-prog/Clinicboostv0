namespace ClinicBoost.Api.Features.Dashboard;

/// <summary>
/// Contrato del servicio de dashboard.
/// Todas las operaciones filtran por tenantId antes de devolver datos.
/// </summary>
public interface IDashboardService
{
    Task<DashboardSummaryResponse> GetSummaryAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    Task<MessageDeliveryResponse> GetMessageDeliveryAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        CancellationToken ct = default);

    Task<FlowPerformanceResponse> GetFlowPerformanceAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        CancellationToken ct = default);

    Task<ConversationsResponse> GetConversationsAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        string? status,
        bool? requiresHuman,
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task<RevenueOverviewResponse> GetRevenueOverviewAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        CancellationToken ct = default);
}
