using BlackFriday.Api.Domain.DTOs;
using BlackFriday.Api.Infrastructure;
using BlackFriday.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Black Friday Multi-Tenant Flash Sale API (SQLite Edition)",
        Version = "v1",
        Description = "High-concurrency multi-tenant flash sale reservation engine using isolated SQLite WAL databases."
    });

    c.AddSecurityDefinition("TenantHeader", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Name = "X-Tenant-ID",
        Type = SecuritySchemeType.ApiKey,
        Description = "Required Tenant Identifier (e.g., tenant-electronics or tenant-fashion)"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "TenantHeader" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TenantDbManager>();
builder.Services.AddSingleton<IDownstreamOrderGateway, SimulatedDownstreamOrderGateway>();
builder.Services.AddSingleton<TenantBackgroundPipeline>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TenantBackgroundPipeline>());
builder.Services.AddScoped<SaleReservationService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbManager = scope.ServiceProvider.GetRequiredService<TenantDbManager>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseSeeder.SeedAsync(dbManager, logger);
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestampUtc = DateTime.UtcNow }));

static bool TryGetTenant(HttpContext ctx, TenantDbManager dbManager, out string tenantId, out IResult? errorResult)
{
    tenantId = ctx.Request.Headers["X-Tenant-ID"].ToString().Trim().ToLowerInvariant();
    if (!TenantDbManager.IsValidTenantIdFormat(tenantId))
    {
        errorResult = Results.BadRequest(new
        {
            error = "Missing or invalid 'X-Tenant-ID' header format."
        });
        return false;
    }

    if (!dbManager.IsRegisteredTenant(tenantId))
    {
        errorResult = Results.Json(
            new
            {
                error = $"Unknown tenant '{tenantId}'. Allowed tenants: {string.Join(", ", dbManager.GetRegisteredTenantIds())}."
            },
            statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    errorResult = null;
    return true;
}

var api = app.MapGroup("/api");

// 1. List all Sale Events for a Tenant
api.MapGet("/sales", async (HttpContext ctx, TenantDbManager dbManager, SaleReservationService service, CancellationToken ct) =>
{
    if (!TryGetTenant(ctx, dbManager, out var tenantId, out var err)) return err!;
    var sales = await service.ListTenantSaleEventsAsync(tenantId, ct);
    return Results.Ok(sales);
})
.WithName("ListTenantSales")
.WithOpenApi();

// 2. Get Current Sale Event Status (Served via Low-Latency Tenant Cache)
api.MapGet("/sales/{saleEventId}", async (
    string saleEventId,
    HttpContext ctx,
    TenantDbManager dbManager,
    SaleReservationService service,
    CancellationToken ct) =>
{
    if (!TryGetTenant(ctx, dbManager, out var tenantId, out var err)) return err!;
    var status = await service.GetSaleEventStatusAsync(tenantId, saleEventId, ct);
    return status is not null ? Results.Ok(status) : Results.NotFound(new { error = "Sale event not found." });
})
.WithName("GetSaleStatus")
.WithOpenApi();

// 3. Create a New Sale Event for a Tenant
api.MapPost("/sales", async (
    CreateSaleEventRequest request,
    HttpContext ctx,
    TenantDbManager dbManager,
    SaleReservationService service,
    CancellationToken ct) =>
{
    if (!TryGetTenant(ctx, dbManager, out var tenantId, out var err)) return err!;
    if (request.InitialStock <= 0 || request.DiscountedPrice <= 0 || request.EndTimeUtc <= request.StartTimeUtc)
    {
        return Results.BadRequest(new { error = "Invalid stock, price, or time window." });
    }

    var created = await service.CreateSaleEventAsync(tenantId, request, ct);
    return Results.Created($"/api/sales/{created.Id}", created);
})
.WithName("CreateSaleEvent")
.WithOpenApi();

// 4. Reserve Stock Atomically & Dispatch Background Order Pipeline Job
api.MapPost("/sales/{saleEventId}/reservations", async (
    string saleEventId,
    CreateReservationRequest request,
    HttpContext ctx,
    TenantDbManager dbManager,
    SaleReservationService service,
    CancellationToken ct) =>
{
    if (!TryGetTenant(ctx, dbManager, out var tenantId, out var err)) return err!;
    var result = await service.ReserveAsync(tenantId, saleEventId, request, ct);

    return result.HttpStatusCode switch
    {
        202 => Results.Accepted($"/api/reservations/{result.ReservationId}", result),
        400 => Results.BadRequest(result),
        404 => Results.NotFound(result),
        409 => Results.Conflict(result),
        _ => Results.StatusCode(result.HttpStatusCode)
    };
})
.WithName("ReserveSaleItem")
.WithOpenApi();

// 5. Get Reservation Status by ID
api.MapGet("/reservations/{reservationId}", async (
    string reservationId,
    HttpContext ctx,
    TenantDbManager dbManager,
    SaleReservationService service,
    CancellationToken ct) =>
{
    if (!TryGetTenant(ctx, dbManager, out var tenantId, out var err)) return err!;
    var reservation = await service.GetReservationByIdAsync(tenantId, reservationId, ct);
    return reservation is not null
        ? Results.Ok(reservation)
        : Results.NotFound(new { error = $"Reservation '{reservationId}' not found for tenant '{tenantId}'." });
})
.WithName("GetReservationStatus")
.WithOpenApi();

// 6. Inspect Pipeline Observability, Orders & Dead-Letter Queue (DLQ) for a Tenant
api.MapGet("/pipeline/status", async (
    HttpContext ctx,
    TenantDbManager dbManager,
    TenantBackgroundPipeline pipeline,
    CancellationToken ct) =>
{
    if (!TryGetTenant(ctx, dbManager, out var tenantId, out var err)) return err!;
    var metrics = pipeline.GetMetrics(tenantId);

    await using var db = await dbManager.CreateDbContextAsync(tenantId, ct);
    var ordersCount = await db.Orders.CountAsync(ct);
    var dlqItems = await db.DeadLetterJobs.AsNoTracking().OrderByDescending(d => d.FailedAtUtc).ToListAsync(ct);

    return Results.Ok(new
    {
        tenantId,
        pipelineMetrics = new
        {
            enqueuedJobs = Interlocked.Read(ref metrics.EnqueuedCount),
            processedJobs = Interlocked.Read(ref metrics.ProcessedCount),
            totalRetryAttempts = Interlocked.Read(ref metrics.RetryCount),
            deadLetterCount = Interlocked.Read(ref metrics.DlqCount)
        },
        persistedOrdersCount = ordersCount,
        deadLetterQueue = dlqItems
    });
})
.WithName("GetTenantPipelineStatus")
.WithOpenApi();

// 7. Replay a Failed Job from the Tenant's Dead-Letter Queue
api.MapPost("/pipeline/dlq/{dlqId}/replay", async (
    string dlqId,
    HttpContext ctx,
    TenantDbManager dbManager,
    TenantBackgroundPipeline pipeline,
    CancellationToken ct) =>
{
    if (!TryGetTenant(ctx, dbManager, out var tenantId, out var err)) return err!;
    await using var db = await dbManager.CreateDbContextAsync(tenantId, ct);
    var writeLock = dbManager.GetTenantWriteLock(tenantId);

    ReservationJob jobToReplay;

    await writeLock.WaitAsync(ct);
    try
    {
        var dlqJob = await db.DeadLetterJobs.FirstOrDefaultAsync(d => d.Id == dlqId && d.TenantId == tenantId, ct);

        if (dlqJob is null) return Results.NotFound(new { error = "DLQ item not found." });
        if (dlqJob.Replayed) return Results.BadRequest(new { error = "DLQ item has already been replayed." });

        dlqJob.Replayed = true;
        await db.SaveChangesAsync(ct);

        jobToReplay = new ReservationJob(
            dlqJob.ReservationId,
            tenantId,
            dlqJob.SaleEventId,
            dlqJob.SkuId,
            dlqJob.CustomerId,
            dlqJob.Quantity,
            dlqJob.TotalPrice,
            SimulateFailure: false);
    }
    finally
    {
        writeLock.Release();
    }

    await pipeline.EnqueueJobAsync(jobToReplay, ct);

    return Results.Accepted(value: new { message = $"DLQ job '{dlqId}' re-queued for processing." });
})
.WithName("ReplayDlqJob")
.WithOpenApi();

app.Run();

public partial class Program { }