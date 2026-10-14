using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlackFriday.Api.Infrastructure;

public class TenantDbManager
{
    private readonly string _dataDir;
    private readonly HashSet<string> _allowedTenants;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantWriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _initializedTenants = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex ValidTenantRegex = new("^[a-zA-Z0-9_-]{3,50}$", RegexOptions.Compiled);

    public TenantDbManager(IConfiguration configuration)
    {
        _dataDir = configuration["Storage:DataDirectory"] ?? "data";
        Directory.CreateDirectory(_dataDir);

        var configuredTenants = configuration.GetSection("Storage:AllowedTenants").Get<string[]>()
            ?? new[] { "tenant-electronics", "tenant-fashion" };

        _allowedTenants = new HashSet<string>(
            configuredTenants.Select(t => t.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsValidTenantIdFormat(string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) && ValidTenantRegex.IsMatch(tenantId);

    public bool IsRegisteredTenant(string? tenantId) =>
        IsValidTenantIdFormat(tenantId) && _allowedTenants.Contains(tenantId!);

    public IReadOnlyCollection<string> GetRegisteredTenantIds() => _allowedTenants;

    public SemaphoreSlim GetTenantWriteLock(string tenantId) =>
        _tenantWriteLocks.GetOrAdd(tenantId.ToLowerInvariant(), _ => new SemaphoreSlim(1, 1));

    public IEnumerable<string> GetExistingTenantIds()
    {
        if (!Directory.Exists(_dataDir))
            return Enumerable.Empty<string>();

        return Directory.GetFiles(_dataDir, "*.db")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name) && IsRegisteredTenant(name))
            .Select(name => name!.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetConnectionString(string tenantId)
    {
        var normalized = tenantId.ToLowerInvariant();
        if (!IsRegisteredTenant(normalized))
        {
            throw new InvalidOperationException($"Unregistered tenant '{tenantId}'.");
        }

        var dbPath = Path.Combine(_dataDir, $"{normalized}.db");
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task<AppDbContext> CreateDbContextAsync(string tenantId, CancellationToken ct = default)
    {
        var normalized = tenantId.ToLowerInvariant();
        if (!IsRegisteredTenant(normalized))
        {
            throw new InvalidOperationException($"Unregistered tenant '{tenantId}'.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite(GetConnectionString(normalized));

        var context = new AppDbContext(optionsBuilder.Options);

        if (!_initializedTenants.ContainsKey(normalized))
        {
            var writeLock = GetTenantWriteLock(normalized);
            await writeLock.WaitAsync(ct);
            try
            {
                if (!_initializedTenants.ContainsKey(normalized))
                {
                    await context.Database.EnsureCreatedAsync(ct);
                    await context.Database.ExecuteSqlRawAsync(
                        "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;", ct);
                    _initializedTenants[normalized] = true;
                }
            }
            finally
            {
                writeLock.Release();
            }
        }

        return context;
    }
}