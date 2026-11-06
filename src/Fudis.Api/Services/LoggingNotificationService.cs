namespace Fudis.Api.Services;

public class LoggingNotificationService : INotificationService
{
    private readonly ILogger<LoggingNotificationService> _log;
    
    public LoggingNotificationService(ILogger<LoggingNotificationService> log) => _log = log;

    public Task NotifyRestaurant(int orderId, CancellationToken ct = default)
    {
        _log.LogInformation("Restaurante notificado del pedido {OrderId}", orderId);
        return Task.CompletedTask;
    }
}