using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClinicBoost.Api.Features.Audit;

/// <summary>
/// Worker en background que limpia JTI revocados expirados cada hora.
/// </summary>
public sealed class SessionCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionCleanupWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public SessionCleanupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SessionCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<ISessionInvalidationService>();
                var deleted = await svc.CleanupExpiredAsync();
                _logger.LogDebug("SessionCleanupWorker: eliminados {Count} registros expirados", deleted);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "SessionCleanupWorker error");
            }
        }
    }
}
