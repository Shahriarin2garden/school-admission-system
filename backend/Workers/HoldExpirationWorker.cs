using SchoolAdmission.Api.Data;

namespace SchoolAdmission.Api.Workers;

/// <summary>
/// Periodically expires overdue HELD applications. Each sweep processes whole
/// classes under the same advisory lock used by hold creation, confirmation and
/// cancellation, so two API instances can run this worker concurrently.
/// </summary>
public sealed class HoldExpirationWorker : BackgroundService
{
    private readonly AdmissionRepository _repository;
    private readonly ILogger<HoldExpirationWorker> _logger;
    private readonly TimeSpan _interval;

    public HoldExpirationWorker(
        AdmissionRepository repository,
        ILogger<HoldExpirationWorker> logger,
        IConfiguration configuration)
    {
        _repository = repository;
        _logger = logger;
        var configured = configuration.GetValue<int?>("EXPIRATION_SWEEP_SECONDS");
        _interval = TimeSpan.FromSeconds(configured is > 0 ? configured.Value : 2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var classIds = await _repository.FindClassesWithExpiredHoldsAsync(50, stoppingToken);
                foreach (var classId in classIds)
                {
                    var expired = await _repository.ExpireOverdueForClassAsync(classId, stoppingToken);
                    if (expired > 0)
                    {
                        _logger.LogInformation("Expired {Count} overdue hold(s) for a class.", expired);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hold expiration sweep failed; it will be retried.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
