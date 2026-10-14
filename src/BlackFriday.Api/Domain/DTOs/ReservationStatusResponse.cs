namespace BlackFriday.Api.Domain.DTOs;

public record ReservationStatusResponse(
    string Id,
    string TenantId,
    string SaleEventId,
    string SkuId,
    string CustomerId,
    int Quantity,
    decimal TotalPrice,
    string Status,
    int RetryCount,
    string? ErrorMessage,
    DateTime CreatedAtUtc,
    DateTime? ProcessedAtUtc);