using System.ComponentModel.DataAnnotations;

namespace BlackFriday.Api.Domain.Entities;

public class OrderRecord
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string SkuId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal TotalAmount { get; set; }
    public bool InventorySynced { get; set; }
    public bool NotificationSent { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}