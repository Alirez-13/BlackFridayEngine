using BlackFriday.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BlackFriday.Api.Infrastructure;

public static class DatabaseSeeder
{
    public static async Task SeedAsync(TenantDbManager dbManager, ILogger logger)
    {
        var now = DateTime.UtcNow;

        var electronicsSales = new List<SaleEvent>
        {
            new()
            {
                Id = "sale-elec-tv65",
                TenantId = "tenant-electronics",
                SkuId = "SKU-ELEC-001",
                ProductName = "65-Inch 4K OLED Smart TV",
                OriginalPrice = 1999.99m,
                DiscountedPrice = 1299.99m,
                InitialStock = 100,
                RemainingStock = 100,
                StartTimeUtc = now.AddMinutes(-15),
                EndTimeUtc = now.AddHours(2)
            },
            new()
            {
                Id = "sale-elec-ps5pro",
                TenantId = "tenant-electronics",
                SkuId = "SKU-ELEC-002",
                ProductName = "PlayStation 5 Pro 2TB Console",
                OriginalPrice = 699.99m,
                DiscountedPrice = 499.99m,
                InitialStock = 75,
                RemainingStock = 75,
                StartTimeUtc = now.AddMinutes(-10),
                EndTimeUtc = now.AddHours(1)
            },
            new()
            {
                Id = "sale-elec-anc-headphones",
                TenantId = "tenant-electronics",
                SkuId = "SKU-ELEC-003",
                ProductName = "Sony WH-1000XM5 Wireless Noise Canceling Headphones",
                OriginalPrice = 399.99m,
                DiscountedPrice = 249.99m,
                InitialStock = 150,
                RemainingStock = 150,
                StartTimeUtc = now.AddMinutes(-30),
                EndTimeUtc = now.AddHours(3)
            },
            new()
            {
                Id = "sale-elec-rtx5090",
                TenantId = "tenant-electronics",
                SkuId = "SKU-ELEC-004",
                ProductName = "NVIDIA GeForce RTX 5090 32GB GPU",
                OriginalPrice = 1999.00m,
                DiscountedPrice = 1599.00m,
                InitialStock = 50,
                RemainingStock = 50,
                StartTimeUtc = now.AddHours(2),
                EndTimeUtc = now.AddHours(4)
            },
            new()
            {
                Id = "sale-elec-macbook-m3",
                TenantId = "tenant-electronics",
                SkuId = "SKU-ELEC-005",
                ProductName = "MacBook Air 15-inch M3 16GB/512GB",
                OriginalPrice = 1499.00m,
                DiscountedPrice = 1049.00m,
                InitialStock = 80,
                RemainingStock = 12,
                StartTimeUtc = now.AddHours(-3),
                EndTimeUtc = now.AddHours(-1)
            }
        };

        var fashionSales = new List<SaleEvent>
        {
            new()
            {
                Id = "sale-fash-leather-jacket",
                TenantId = "tenant-fashion",
                SkuId = "SKU-FASH-001",
                ProductName = "Italian Lambskin Biker Jacket",
                OriginalPrice = 450.00m,
                DiscountedPrice = 225.00m,
                InitialStock = 100,
                RemainingStock = 100,
                StartTimeUtc = now.AddMinutes(-20),
                EndTimeUtc = now.AddHours(2)
            },
            new()
            {
                Id = "sale-fash-cashmere-coat",
                TenantId = "tenant-fashion",
                SkuId = "SKU-FASH-002",
                ProductName = "Pure Cashmere Winter Trench Coat",
                OriginalPrice = 890.00m,
                DiscountedPrice = 534.00m,
                InitialStock = 60,
                RemainingStock = 60,
                StartTimeUtc = now.AddMinutes(-15),
                EndTimeUtc = now.AddHours(2)
            },
            new()
            {
                Id = "sale-fash-sneakers-ltd",
                TenantId = "tenant-fashion",
                SkuId = "SKU-FASH-003",
                ProductName = "Limited Edition Retro High-Top Sneakers",
                OriginalPrice = 220.00m,
                DiscountedPrice = 139.00m,
                InitialStock = 200,
                RemainingStock = 200,
                StartTimeUtc = now.AddMinutes(-5),
                EndTimeUtc = now.AddHours(1)
            },
            new()
            {
                Id = "sale-fash-silk-dress",
                TenantId = "tenant-fashion",
                SkuId = "SKU-FASH-004",
                ProductName = "Designer Mulberry Silk Evening Gown",
                OriginalPrice = 650.00m,
                DiscountedPrice = 390.00m,
                InitialStock = 90,
                RemainingStock = 90,
                StartTimeUtc = now.AddHours(1),
                EndTimeUtc = now.AddHours(3)
            },
            new()
            {
                Id = "sale-fash-tote-bag",
                TenantId = "tenant-fashion",
                SkuId = "SKU-FASH-005",
                ProductName = "Handcrafted Saffiano Leather Tote Bag",
                OriginalPrice = 380.00m,
                DiscountedPrice = 190.00m,
                InitialStock = 120,
                RemainingStock = 0,
                StartTimeUtc = now.AddHours(-4),
                EndTimeUtc = now.AddHours(-2)
            }
        };

        await SeedTenantAsync(dbManager, "tenant-electronics", electronicsSales, logger);
        await SeedTenantAsync(dbManager, "tenant-fashion", fashionSales, logger);
    }

    private static async Task SeedTenantAsync(
        TenantDbManager dbManager,
        string tenantId,
        List<SaleEvent> events,
        ILogger logger)
    {
        await using var db = await dbManager.CreateDbContextAsync(tenantId);
        var writeLock = dbManager.GetTenantWriteLock(tenantId);
        await writeLock.WaitAsync();
        try
        {
            foreach (var ev in events)
            {
                var existing = await db.SaleEvents.FirstOrDefaultAsync(x => x.Id == ev.Id);
                if (existing is null)
                {
                    db.SaleEvents.Add(ev);
                }
                else
                {
                    // Refresh relative time windows on container restart so active/upcoming sales stay valid
                    existing.StartTimeUtc = ev.StartTimeUtc;
                    existing.EndTimeUtc = ev.EndTimeUtc;
                }
            }

            await db.SaveChangesAsync();
            logger.LogInformation("Seeded/verified tenant '{TenantId}' with {Count} sale events.", tenantId,
                events.Count);
        }
        finally
        {
            writeLock.Release();
        }
    }
}