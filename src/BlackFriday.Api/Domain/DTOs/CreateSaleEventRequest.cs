namespace BlackFriday.Api.Domain.DTOs;

public record CreateSaleEventRequest(
    string? Id,
    string SkuId,
    string ProductName,
    decimal OriginalPrice,
    decimal DiscountedPrice,
    int InitialStock,
    DateTime StartTimeUtc,
    DateTime EndTimeUtc);