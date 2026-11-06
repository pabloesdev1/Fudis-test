using System.Text.Json;
using Fudis.Api.Contracts;
using Fudis.Api.Data;
using Fudis.Api.Domain;
using Fudis.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Api.Controllers;

[ApiController]
public class PaymentsController : ControllerBase
{
    private readonly FudisDbContext _db;
    private readonly ILogger<PaymentsController> _log;

    public PaymentsController(FudisDbContext db, ILogger<PaymentsController> log)
    {
        _db = db;
        _log = log;
    }

    [HttpPost("confirm-payment")]
    public async Task<IActionResult> ConfirmPayment(PaymentNotification request, CancellationToken ct)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
        if (order is null)
            return NotFound();

        // Duplicado ya procesado: respuesta idempotente
        if (await _db.PaymentTransactions.AnyAsync(p => p.TransactionId == request.TransactionId, ct))
            return Ok();

        // PENDIENTE: validar que request.Amount y request.Currency coincidan con el pedido.

        if (order.Status != OrderStatus.Pending)
            return Conflict(new { error = $"El pedido está en estado {order.Status}" });

        // Todo se guarda en UNA sola transacción (un único SaveChanges)
        _db.PaymentTransactions.Add(new PaymentTransaction
        {
            OrderId = order.Id,
            TransactionId = request.TransactionId,
            Amount = request.Amount,
            Currency = request.Currency.ToUpperInvariant()
        });

        var earning = order.MarkAsPaid();
        if (earning is not null)
            _db.DriverEarnings.Add(earning);

        _db.OutboxMessages.Add(new OutboxMessage
        {
            Type = "NotifyRestaurant",
            Payload = JsonSerializer.Serialize(new { orderId = order.Id })
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException
            || ex is DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } })
        {
            // Otro hilo ganó la carrera: si procesó este mismo pago, es un duplicado.
            _db.ChangeTracker.Clear();
            if (await _db.PaymentTransactions.AnyAsync(p => p.TransactionId == request.TransactionId, ct))
                return Ok();

            _log.LogWarning("Conflicto concurrente en el pedido {OrderId}", request.OrderId);
            return Conflict(new { error = "Conflicto concurrente, reintente" });
        }

        return Ok();
    }
}