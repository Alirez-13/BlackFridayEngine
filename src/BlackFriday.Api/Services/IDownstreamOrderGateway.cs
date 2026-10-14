using BlackFriday.Api.Domain.DTOs;

namespace BlackFriday.Api.Services;

public interface IDownstreamOrderGateway
{
    Task<(bool InventorySynced, bool NotificationSent)> FulfillOrderAsync(ReservationJob job, CancellationToken ct = default);
}

public class SimulatedDownstreamOrderGateway : IDownstreamOrderGateway
{
    public async Task<(bool InventorySynced, bool NotificationSent)> FulfillOrderAsync(
        ReservationJob job,
        CancellationToken ct = default)
    {
        if (job.SimulateFailure)
        {
            throw new InvalidOperationException(
                $"Simulated downstream warehouse/order gateway outage for reservation '{job.ReservationId}'.");
        }

        // Stage 1: Stubbed Warehouse Inventory Sync
        await Task.Delay(15, ct);

        // Stage 2: Stubbed Customer Notification Dispatch
        await Task.Delay(10, ct);

        return (InventorySynced: true, NotificationSent: true);
    }
}