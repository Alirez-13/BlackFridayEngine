using System.ComponentModel.DataAnnotations;

namespace BlackFriday.Api.Domain.Entities;

public class SaleEvent
{
    [Key]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string SkuId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal OriginalPrice { get; set; }
    public decimal DiscountedPrice { get; set; }
    public int InitialStock { get; set; }
    public int RemainingStock { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string GetComputedStatus(DateTime nowUtc)
    {
        if (nowUtc < StartTimeUtc) return "Upcoming";
        if (nowUtc > EndTimeUtc) return "Expired";
        if (RemainingStock <= 0) return "SoldOut";
        return "Active";
    }
}