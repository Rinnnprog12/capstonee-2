using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Documents.Services;

namespace TsuOrg.Infrastructure.Calsv;

/// <summary>
/// Background worker that dequeues CALSV jobs and processes each in a fresh DI scope.
/// </summary>
public sealed class CalsvBackgroundService : BackgroundService
{
    private readonly ICalsvJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CalsvBackgroundService> _logger;

    public CalsvBackgroundService(
        ICalsvJobQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<CalsvBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CALSV background worker started");

        await foreach (var job in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ICalsvOrchestrator>();
                await orchestrator.ProcessAsync(job.DocumentId, job.TriggeredByUserId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled CALSV job failure for document {DocumentId}", job.DocumentId);
            }
        }
    }
}
