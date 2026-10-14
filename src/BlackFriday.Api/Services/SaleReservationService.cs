using BlackFriday.Api.Domain.DTOs;
using BlackFriday.Api.Domain.Entities;
using BlackFriday.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BlackFriday.Api.Services;

public class SaleReservationService
{
    private readonly TenantDbManager _dbManager;
    private readonly TenantBackgroundPipeline _pipeline;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SaleReservationService> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    public SaleReservationService(
        TenantDbManager dbManager,
        TenantBackgroundPipeline pipeline,
        IMemoryCache cache,
        ILogger<SaleReservationService> logger)
    {
        _dbManager = dbManager;
        _pipeline = pipeline;
        _cache = cache;
        _logger = logger;
    }

    private static string GetSaleCacheKey(string tenantId, string saleId) =>
        $"tenant:{tenantId.ToLowerInvariant()}:sale:{saleId.ToLowerInvariant()}";

    private static string GetTenantSalesListCacheKey(string tenantId) =>
        $"tenant:{tenantId.ToLowerInvariant()}:sales:all";

    public async Task<SaleEventStatusResponse> CreateSaleEventAsync(
        string tenantId,
        CreateSaleEventRequest request,
        CancellationToken ct = default)
    {
        var normalizedTenant = tenantId.ToLowerInvariant();
        var saleEvent = new SaleEvent
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? $"sale-{Guid.NewGuid():N}"[..16] : request.Id,
            TenantId = normalizedTenant,
            SkuId = request.SkuId,
            ProductName = request.ProductName,
            OriginalPrice = request.OriginalPrice,
            DiscountedPrice = request.DiscountedPrice,
            InitialStock = request.InitialStock,
            RemainingStock = request.InitialStock,
            StartTimeUtc = request.StartTimeUtc.ToUniversalTime(),
            EndTimeUtc = request.EndTimeUtc.ToUniversalTime(),
            CreatedAtUtc = DateTime.UtcNow
        };

        await using var db = await _dbManager.CreateDbContextAsync(normalizedTenant, ct);
        var writeLock = _dbManager.GetTenantWriteLock(normalizedTenant);
        await writeLock.WaitAsync(ct);
        try
        {
            db.SaleEvents.Add(saleEvent);
            await db.SaveChangesAsync(ct);

            _cache.Set(GetSaleCacheKey(normalizedTenant, saleEvent.Id), saleEvent, CacheTtl);
            _cache.Remove(GetTenantSalesListCacheKey(normalizedTenant));
        }
        finally
        {
            writeLock.Release();
        }

        return MapToStatusResponse(saleEvent, DateTime.UtcNow);
    }

    public async Task<SaleEventStatusResponse?> GetSaleEventStatusAsync(
        string tenantId,
        string saleEventId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var normalizedTenant = tenantId.ToLowerInvariant();
        var cacheKey = GetSaleCacheKey(normalizedTenant, saleEventId);

        if (!_cache.TryGetValue(cacheKey, out SaleEvent? saleEvent) || saleEvent is null)
        {
            await using var db = await _dbManager.CreateDbContextAsync(normalizedTenant, ct);
            saleEvent = await db.SaleEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == saleEventId && e.TenantId == normalizedTenant, ct);

            if (saleEvent is null) return null;

            _cache.Set(cacheKey, saleEvent, CacheTtl);
        }

        return MapToStatusResponse(saleEvent, now);
    }

    public async Task<List<SaleEventStatusResponse>> ListTenantSaleEventsAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var normalizedTenant = tenantId.ToLowerInvariant();
        var listCacheKey = GetTenantSalesListCacheKey(normalizedTenant);

        if (!_cache.TryGetValue(listCacheKey, out List<SaleEvent>? events) || events is null)
        {
            await using var db = await _dbManager.CreateDbContextAsync(normalizedTenant, ct);
            events = await db.SaleEvents
                .AsNoTracking()
                .OrderBy(e => e.StartTimeUtc)
                .ToListAsync(ct);

            _cache.Set(listCacheKey, events, CacheTtl);
        }

        return events.Select(e => MapToStatusResponse(e, now)).ToList();
    }

    public async Task<ReservationStatusResponse?> GetReservationByIdAsync(
        string tenantId,
        string reservationId,
        CancellationToken ct = default)
    {
        var normalizedTenant = tenantId.ToLowerInvariant();
        await using var db = await _dbManager.CreateDbContextAsync(normalizedTenant, ct);
        var res = await db.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId && r.TenantId == normalizedTenant, ct);

        if (res is null) return null;

        return new ReservationStatusResponse(
            res.Id,
            res.TenantId,
            res.SaleEventId,
            res.SkuId,
            res.CustomerId,
            res.Quantity,
            res.TotalPrice,
            res.Status,
            res.RetryCount,
            res.ErrorMessage,
            res.CreatedAtUtc,
            res.ProcessedAtUtc);
    }

    public async Task<ReservationResult> ReserveAsync(
        string tenantId,
        string saleEventId,
        CreateReservationRequest request,
        CancellationToken ct = default)
    {
        if (request.Quantity <= 0)
        {
            return new ReservationResult(false, 400, "Quantity must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            return new ReservationResult(false, 400, "CustomerId is required.");
        }

        var normalizedTenant = tenantId.ToLowerInvariant();
        var now = DateTime.UtcNow;

        await using var db = await _dbManager.CreateDbContextAsync(normalizedTenant, ct);
        var writeLock = _dbManager.GetTenantWriteLock(normalizedTenant);

        Reservation reservation;
        SaleEvent updatedSale;

        await writeLock.WaitAsync(ct);
        try
        {
            int rowsAffected = await db.SaleEvents
                .Where(e => e.Id == saleEventId
                         && e.TenantId == normalizedTenant
                         && e.StartTimeUtc <= now
                         && e.EndTimeUtc >= now
                         && e.RemainingStock >= request.Quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    e => e.RemainingStock,
                    e => e.RemainingStock - request.Quantity), ct);

            if (rowsAffected == 0)
            {
                var existing = await db.SaleEvents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == saleEventId && e.TenantId == normalizedTenant, ct);

                if (existing is null)
                    return new ReservationResult(false, 404, $"Sale event '{saleEventId}' not found for tenant '{normalizedTenant}'.");

                if (now < existing.StartTimeUtc)
                    return new ReservationResult(false, 400, "Sale event has not started yet (Upcoming).");

                if (now > existing.EndTimeUtc)
                    return new ReservationResult(false, 400, "Sale event has already ended (Expired).");

                return new ReservationResult(false, 409, "Sold out or insufficient remaining stock.", null, existing.RemainingStock);
            }

            updatedSale = await db.SaleEvents
                .AsNoTracking()
                .FirstAsync(e => e.Id == saleEventId && e.TenantId == normalizedTenant, ct);

            reservation = new Reservation
            {
                Id = $"res-{Guid.NewGuid():N}"[..16],
                TenantId = normalizedTenant,
                SaleEventId = saleEventId,
                SkuId = updatedSale.SkuId,
                CustomerId = request.CustomerId,
                Quantity = request.Quantity,
                TotalPrice = updatedSale.DiscountedPrice * request.Quantity,
                Status = "Reserved",
                CreatedAtUtc = now
            };

            db.Reservations.Add(reservation);
            await db.SaveChangesAsync(ct);

            // Immediate Write-Through Cache Update & List Cache Invalidation
            _cache.Set(GetSaleCacheKey(normalizedTenant, saleEventId), updatedSale, CacheTtl);
            _cache.Remove(GetTenantSalesListCacheKey(normalizedTenant));
        }
        finally
        {
            writeLock.Release();
        }

        var job = new ReservationJob(
            reservation.Id,
            normalizedTenant,
            saleEventId,
            updatedSale.SkuId,
            request.CustomerId,
            request.Quantity,
            reservation.TotalPrice,
            request.SimulateDownstreamFailure);

        await _pipeline.EnqueueJobAsync(job, ct);

        return new ReservationResult(
            true,
            202,
            "Reservation accepted. Order processing queued in background pipeline.",
            reservation.Id,
            updatedSale.RemainingStock);
    }

    private static SaleEventStatusResponse MapToStatusResponse(SaleEvent e, DateTime nowUtc)
    {
        var status = e.GetComputedStatus(nowUtc);
        return new SaleEventStatusResponse(
            e.Id,
            e.TenantId,
            e.SkuId,
            e.ProductName,
            e.OriginalPrice,
            e.DiscountedPrice,
            e.InitialStock,
            e.RemainingStock,
            status,
            status == "Active",
            e.StartTimeUtc,
            e.EndTimeUtc,
            nowUtc);
    }
}