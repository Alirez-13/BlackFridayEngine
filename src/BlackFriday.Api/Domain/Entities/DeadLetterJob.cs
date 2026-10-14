using System.ComponentModel.DataAnnotations;

namespace BlackFriday.Api.Domain.Entities;

public class DeadLetterJob
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string SaleEventId { get; set; } = string.Empty;
    public string SkuId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal TotalPrice { get; set; }
    public string FailureReason { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public bool Replayed { get; set; }
    public DateTime FailedAtUtc { get; set; } = DateTime.UtcNow;
}