namespace Fudis.Api.Services;

public interface INotificationService
{
    Task NotifyRestaurant(int orderId, CancellationToken ct = default);
}