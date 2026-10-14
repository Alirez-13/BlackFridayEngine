using System.Net;
using System.Net.Http.Json;
using BlackFriday.Api.Domain.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlackFriday.Tests;

public class SaleReservationConcurrencyTests : IAsyncLifetime
{
    private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"bf_tests_{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:DataDirectory"] = _tempDataDir
                });
            });
        });

        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_tempDataDir))
        {
            try { Directory.Delete(_tempDataDir, recursive: true); } catch { /* ignore cleanup locks */ }
        }
    }

    [Fact]
    public async Task ReserveAsync_150ConcurrentRequestsFor100Stock_NeverOversells()
    {
        // Arrange
        var saleId = $"sale-conc-{Guid.NewGuid():N}"[..16];
        using var createMsg = new HttpRequestMessage(HttpMethod.Post, "/api/sales");
        createMsg.Headers.Add("X-Tenant-ID", "tenant-electronics");
        createMsg.Content = JsonContent.Create(new CreateSaleEventRequest(
            Id: saleId,
            SkuId: "SKU-TEST-100",
            ProductName: "Concurrent Test GPU",
            OriginalPrice: 1000m,
            DiscountedPrice: 750m,
            InitialStock: 100,
            StartTimeUtc: DateTime.UtcNow.AddMinutes(-10),
            EndTimeUtc: DateTime.UtcNow.AddHours(1)));

        var createRes = await _client.SendAsync(createMsg);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        // Act: Fire 150 concurrent reservation requests for 1 unit each
        var tasks = Enumerable.Range(1, 150).Select(async i =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sales/{saleId}/reservations");
            req.Headers.Add("X-Tenant-ID", "tenant-electronics");
            req.Content = JsonContent.Create(new CreateReservationRequest($"cust-{i}", Quantity: 1));
            return await _client.SendAsync(req);
        });

        var responses = await Task.WhenAll(tasks);

        // Assert: Exactly 100 Accepted (202) and 50 Conflict (409)
        var acceptedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(100, acceptedCount);
        Assert.Equal(50, conflictCount);

        // Verify final remaining stock is 0
        using var statusReq = new HttpRequestMessage(HttpMethod.Get, $"/api/sales/{saleId}");
        statusReq.Headers.Add("X-Tenant-ID", "tenant-electronics");
        var statusRes = await _client.SendAsync(statusReq);
        var statusBody = await statusRes.Content.ReadFromJsonAsync<SaleEventStatusResponse>();

        Assert.NotNull(statusBody);
        Assert.Equal(0, statusBody.RemainingStock);
        Assert.Equal("SoldOut", statusBody.Status);
    }

    [Fact]
    public async Task UnknownTenant_Returns403Forbidden_AndDoesNotCreateDatabaseFile()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sales");
        req.Headers.Add("X-Tenant-ID", "random-unregistered-tenant");

        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var rogueDbPath = Path.Combine(_tempDataDir, "random-unregistered-tenant.db");
        Assert.False(File.Exists(rogueDbPath));
    }
}