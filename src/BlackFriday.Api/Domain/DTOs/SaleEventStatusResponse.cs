namespace BlackFriday.Api.Domain.DTOs;

public record SaleEventStatusResponse(
    string Id,
    string TenantId,
    string SkuId,
    string ProductName,
    decimal OriginalPrice,
    decimal DiscountedPrice,
    int InitialStock,
    int RemainingStock,
    string Status,
    bool IsActive,
    DateTime StartTimeUtc,
    DateTime EndTimeUtc,
    DateTime CachedAtUtc);