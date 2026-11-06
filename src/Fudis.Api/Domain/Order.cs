namespace Fudis.Api.Domain;

public enum OrderStatus
{
    Pending = 0,
    Paid = 1,
    Cancelled = 2
}

public class Order
{
    public const decimal DriverShare = 0.70m;

    public int Id { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public int? DriverId { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal Total { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = default!;   // token de concurrencia

    /// Marca el pedido como pagado. Devuelve la ganancia del repartidor,
    /// o null si aún no hay repartidor asignado.
    public DriverEarning? MarkAsPaid()
    {
        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException("No se puede pagar un pedido cancelado.");

        if (Status == OrderStatus.Paid)
            throw new InvalidOperationException("El pedido ya está pagado.");

        Status = OrderStatus.Paid;

        if (DriverId is not int driverId)
            return null;

        return new DriverEarning
        {
            OrderId  = Id,
            DriverId = driverId,
            Amount   = Math.Round(DeliveryFee * DriverShare, 2, MidpointRounding.ToZero)
        };
    }
}