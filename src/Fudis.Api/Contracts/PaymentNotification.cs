using System.ComponentModel.DataAnnotations;

namespace Fudis.Api.Contracts;

public class PaymentNotification
{
    [Required] public int OrderId { get; set; }
    [Required, StringLength(100)] public string TransactionId { get; set; } = default!;
    [Range(0.01, double.MaxValue)] public decimal Amount { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = default!;
}