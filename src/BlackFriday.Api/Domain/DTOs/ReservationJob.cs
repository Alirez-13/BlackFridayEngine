namespace BlackFriday.Api.Domain.DTOs;

public record ReservationJob(
    string ReservationId,
    string TenantId,
    string SaleEventId,
    string SkuId,
    string CustomerId,
    int Quantity,
    decimal TotalPrice,
    bool SimulateFailure);