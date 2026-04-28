using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIPolicyEngine.Api.Models;
using AIPolicyEngine.Api.Services;
using StackExchange.Redis;

namespace AIPolicyEngine.Api.Endpoints;

/// <summary>
/// WebSocket endpoint for streaming real-time log updates.
/// Replaces the Python Quart WebSocket (ws/logs).
/// </summary>
public static class WebSocketEndpoints
{
    public static IEndpointRouteBuilder MapWebSocketEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.Map("/ws/logs", async (HttpContext context, ILogDataService logDataService, ILogger<LogsResponse> logger) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket connection required");
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("WebSocket client connected for /ws/logs");

            try
            {
                using var cts = new CancellationTokenSource();

                while (ws.State == WebSocketState.Open)
                {
                    var logs = await logDataService.GetAllLogsAsync(logger);

                    var payload = JsonSerializer.Serialize(new LogsResponse { AggregatedLogs = logs }, JsonConfig.Default);
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

                    var buffer = new byte[256];
                    var receiveTask = ws.ReceiveAsync(buffer, cts.Token);
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    var completed = await Task.WhenAny(receiveTask, delayTask);

                    if (completed == receiveTask)
                    {
                        var result = await receiveTask;
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected", CancellationToken.None);
                            break;
                        }
                    }
                }
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "WebSocket connection closed unexpectedly");
            }
        });

        return routes;
    }
}
