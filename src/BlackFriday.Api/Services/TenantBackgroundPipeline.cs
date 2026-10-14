using System.Collections.Concurrent;
using System.Threading.Channels;
using BlackFriday.Api.Domain.DTOs;
using BlackFriday.Api.Domain.Entities;
using BlackFriday.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BlackFriday.Api.Services;

public class TenantBackgroundPipeline : IHostedService
{
    private readonly TenantDbManager _dbManager;
    private readonly IDownstreamOrderGateway _downstreamGateway;
    private readonly ILogger<TenantBackgroundPipeline> _logger;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, Channel<ReservationJob>> _tenantChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _tenantWorkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TenantPipelineMetrics> _tenantMetrics = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxRetryAttempts = 3;

    public TenantBackgroundPipeline(
        TenantDbManager dbManager,
        IDownstreamOrderGateway downstreamGateway,
        ILogger<TenantBackgroundPipeline> logger)
    {
        _dbManager = dbManager;
        _downstreamGateway = downstreamGateway;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Multi-Tenant Background Pipeline Manager started.");
        await RecoverPendingReservationsAsync(cancellationToken);
    }

    private async Task RecoverPendingReservationsAsync(CancellationToken ct)
    {
        foreach (var tenantId in _dbManager.GetExistingTenantIds())
        {
            await using var db = await _dbManager.CreateDbContextAsync(tenantId, ct);
            var pendingReservations = await db.Reservations
                .AsNoTracking()
                .Where(r => r.Status == "Reserved")
                .OrderBy(r => r.CreatedAtUtc)
                .ToListAsync(ct);

            foreach (var res in pendingReservations)
            {
                await EnqueueJobAsync(new ReservationJob(
                    res.Id,
                    tenantId,
                    res.SaleEventId,
                    res.SkuId,
                    res.CustomerId,
                    res.Quantity,
                    res.TotalPrice,
                    SimulateFailure: false), ct);
            }

            if (pendingReservations.Count > 0)
            {
                _logger.LogInformation(
                    "Recovered and re-queued {Count} pending reservation jobs for tenant '{TenantId}'.",
                    pendingReservations.Count, tenantId);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        foreach (var ch in _tenantChannels.Values)
        {
            ch.Writer.TryComplete();
        }
        await Task.WhenAll(_tenantWorkers.Values);
    }

    public TenantPipelineMetrics GetMetrics(string tenantId) =>
        _tenantMetrics.GetOrAdd(tenantId.ToLowerInvariant(), _ => new TenantPipelineMetrics());

    public async ValueTask EnqueueJobAsync(ReservationJob job, CancellationToken ct = default)
    {
        var normalizedTenant = job.TenantId.ToLowerInvariant();
        var metrics = GetMetrics(normalizedTenant);
        var channel = GetOrCreateTenantChannel(normalizedTenant);

        await channel.Writer.WriteAsync(job, ct);
        Interlocked.Increment(ref metrics.EnqueuedCount);
    }

    private Channel<ReservationJob> GetOrCreateTenantChannel(string tenantId)
    {
        return _tenantChannels.GetOrAdd(tenantId, t =>
        {
            var channel = Channel.CreateUnbounded<ReservationJob>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _tenantWorkers[t] = Task.Run(() => RunTenantWorkerLoopAsync(t, channel.Reader, _cts.Token));
            _logger.LogInformation("Initialized isolated background worker pipeline for tenant '{TenantId}'.", t);
            return channel;
        });
    }

    private async Task RunTenantWorkerLoopAsync(string tenantId, ChannelReader<ReservationJob> reader, CancellationToken ct)
    {
        var metrics = GetMetrics(tenantId);

        try
        {
            await foreach (var job in reader.ReadAllAsync(ct))
            {
                await ProcessJobWithRetryAsync(tenantId, job, metrics, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Worker pipeline for tenant '{TenantId}' shutting down.", tenantId);
        }
    }

    private async Task ProcessJobWithRetryAsync(
        string tenantId,
        ReservationJob job,
        TenantPipelineMetrics metrics,
        CancellationToken ct)
    {
        int attempt = 0;
        Exception? lastException = null;

        while (attempt < MaxRetryAttempts)
        {
            attempt++;
            try
            {
                await ExecuteDownstreamOrderStagesAsync(tenantId, job, attempt, ct);
                Interlocked.Increment(ref metrics.ProcessedCount);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Interlocked.Increment(ref metrics.RetryCount);
                _logger.LogWarning(
                    "Tenant '{TenantId}' | Reservation '{ReservationId}' failed on attempt {Attempt}/{MaxRetries}: {Error}",
                    tenantId, job.ReservationId, attempt, MaxRetryAttempts, ex.Message);

                if (attempt < MaxRetryAttempts)
                {
                    var delayMs = 150 * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delayMs, ct);
                }
            }
        }

        await RouteToDeadLetterQueueAsync(tenantId, job, attempt, lastException?.Message ?? "Unknown error", metrics, ct);
    }

    private async Task ExecuteDownstreamOrderStagesAsync(
        string tenantId,
        ReservationJob job,
        int attempt,
        CancellationToken ct)
    {
        var (inventorySynced, notificationSent) = await _downstreamGateway.FulfillOrderAsync(job, ct);

        await using var db = await _dbManager.CreateDbContextAsync(tenantId, ct);
        var writeLock = _dbManager.GetTenantWriteLock(tenantId);
        await writeLock.WaitAsync(ct);
        try
        {
            var existingOrder = await db.Orders.AnyAsync(o => o.ReservationId == job.ReservationId, ct);
            if (!existingOrder)
            {
                db.Orders.Add(new OrderRecord
                {
                    TenantId = tenantId,
                    ReservationId = job.ReservationId,
                    SkuId = job.SkuId,
                    CustomerId = job.CustomerId,
                    Quantity = job.Quantity,
                    TotalAmount = job.TotalPrice,
                    InventorySynced = inventorySynced,
                    NotificationSent = notificationSent
                });
            }

            await db.Reservations
                .Where(r => r.Id == job.ReservationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, "Confirmed")
                    .SetProperty(r => r.RetryCount, attempt - 1)
                    .SetProperty(r => r.ErrorMessage, (string?)null)
                    .SetProperty(r => r.ProcessedAtUtc, DateTime.UtcNow), ct);

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }

        _logger.LogInformation(
            "Tenant '{TenantId}' | Completed Order for Reservation '{ReservationId}' (SKU: {SkuId}, Customer: {CustomerId}).",
            tenantId, job.ReservationId, job.SkuId, job.CustomerId);
    }

    private async Task RouteToDeadLetterQueueAsync(
        string tenantId,
        ReservationJob job,
        int attempts,
        string errorReason,
        TenantPipelineMetrics metrics,
        CancellationToken ct)
    {
        Interlocked.Increment(ref metrics.DlqCount);

        await using var db = await _dbManager.CreateDbContextAsync(tenantId, ct);
        var writeLock = _dbManager.GetTenantWriteLock(tenantId);
        await writeLock.WaitAsync(ct);
        try
        {
            db.DeadLetterJobs.Add(new DeadLetterJob
            {
                TenantId = tenantId,
                ReservationId = job.ReservationId,
                SaleEventId = job.SaleEventId,
                SkuId = job.SkuId,
                CustomerId = job.CustomerId,
                Quantity = job.Quantity,
                TotalPrice = job.TotalPrice,
                FailureReason = errorReason,
                Attempts = attempts,
                FailedAtUtc = DateTime.UtcNow
            });

            await db.Reservations
                .Where(r => r.Id == job.ReservationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, "FailedToDlq")
                    .SetProperty(r => r.RetryCount, attempts)
                    .SetProperty(r => r.ErrorMessage, errorReason)
                    .SetProperty(r => r.ProcessedAtUtc, DateTime.UtcNow), ct);

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }

        _logger.LogError(
            "Tenant '{TenantId}' | Reservation '{ReservationId}' exhausted {Attempts} attempts and moved to DLQ. Reason: {Reason}",
            tenantId, job.ReservationId, attempts, errorReason);
    }
}