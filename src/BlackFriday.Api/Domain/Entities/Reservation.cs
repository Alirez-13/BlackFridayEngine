using System.ComponentModel.DataAnnotations;

namespace BlackFriday.Api.Domain.Entities;

public class Reservation
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string SaleEventId { get; set; } = string.Empty;
    public string SkuId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal TotalPrice { get; set; }
    public string Status { get; set; } = "Reserved"; // Reserved, Confirmed, FailedToDlq
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
}