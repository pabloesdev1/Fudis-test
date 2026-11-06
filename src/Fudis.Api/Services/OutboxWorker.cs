using System.Text.Json;
using Fudis.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Api.Services;


public class OutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxWorker> _log;

    public OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await ProcessBatchAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Error procesando el outbox");
            }
        }
    }

    // Entrega "al menos una vez"
    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FudisDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var pending = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.Attempts < 10)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        foreach (var m in pending)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Payload);
                await notifier.NotifyRestaurant(doc.RootElement.GetProperty("orderId").GetInt32(), ct);
                m.ProcessedAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.Attempts++;
                _log.LogWarning(ex, "Falló el mensaje outbox {Id} (intento {N})", m.Id, m.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}