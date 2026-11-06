namespace Fudis.Api.Domain;

public class PaymentTransaction
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string TransactionId { get; set; } = default!;  // id del pago en la pasarela
    public decimal Amount { get; set; }
    public string Currency { get; set; } = default!;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}