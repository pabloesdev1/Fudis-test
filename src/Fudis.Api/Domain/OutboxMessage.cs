namespace Fudis.Api.Domain;

public class OutboxMessage
{
    public int Id { get; set; }
    public string Type { get; set; } = default!;      // ej. "NotifyRestaurant"
    public string Payload { get; set; } = default!;   // JSON, ej. {"orderId":5}
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }        // null = pendiente
    public int Attempts { get; set; }
}