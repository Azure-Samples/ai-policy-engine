using System.Threading.Channels;

namespace AIPolicyEngine.Api.Services.ApimManagement;

public sealed class ApimPolicyApplyBackgroundService : BackgroundService
{
    private readonly Channel<ApimPolicyApplyWorkItem> _channel;
    private readonly ApimPolicyApplyService _applyService;
    private readonly ILogger<ApimPolicyApplyBackgroundService> _logger;

    public ApimPolicyApplyBackgroundService(
        Channel<ApimPolicyApplyWorkItem> channel,
        ApimPolicyApplyService applyService,
        ILogger<ApimPolicyApplyBackgroundService> logger)
    {
        _channel = channel;
        _applyService = applyService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("APIM policy apply background service started");

        try
        {
            var recoverableItems = await _applyService.ListRecoverableWorkItemsAsync(stoppingToken);
            foreach (var item in recoverableItems)
            {
                _channel.Writer.TryWrite(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replay pending APIM policy assignments on startup; continuing without replay");
        }

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _applyService.ProcessAssignmentAsync(item.ApiId, item.OperationId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing APIM policy assignment for ApiId={ApiId} OperationId={OperationId}", item.ApiId, item.OperationId);
            }
        }

        _logger.LogInformation("APIM policy apply background service stopped");
    }
}
