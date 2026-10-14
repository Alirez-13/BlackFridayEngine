namespace BlackFriday.Api.Domain.DTOs;

public record ReservationResult(
    bool Success,
    int HttpStatusCode,
    string Message,
    string? ReservationId = null,
    int? RemainingStockAfterReservation = null);