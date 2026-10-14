namespace BlackFriday.Api.Domain.DTOs;

public record CreateReservationRequest(
    string CustomerId,
    int Quantity = 1,
    bool SimulateDownstreamFailure = false);