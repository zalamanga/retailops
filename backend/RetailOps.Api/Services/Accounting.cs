using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Data;
using RetailOps.Api.Domain;

namespace RetailOps.Api.Services;

public static class Accounting
{
    public static void Post(AppDbContext db, string sourceType, Guid sourceId, Guid? branchId, string description,
        params (string Account, decimal Debit, decimal Credit)[] components)
        => PostAt(db, sourceType, sourceId, branchId, description, DateTimeOffset.UtcNow, components);
    public static void PostAt(AppDbContext db, string sourceType, Guid sourceId, Guid? branchId, string description, DateTimeOffset postedAt,
        params (string Account, decimal Debit, decimal Credit)[] components)
    {
        var lines = components.Where(x => x.Debit != 0 || x.Credit != 0).ToArray();
        if (lines.Length == 0) return;
        if (lines.Any(x => x.Debit < 0 || x.Credit < 0 || (x.Debit > 0) == (x.Credit > 0)) || lines.Sum(x => x.Debit) != lines.Sum(x => x.Credit))
            throw new InvalidOperationException("A posting must contain positive, balanced journal lines.");
        var entry = new JournalEntry { Number = Number("JE"), SourceType = sourceType, SourceId = sourceId, BranchId = branchId, Description = description, CreatedAt = postedAt };
        entry.Lines = lines.Select(x => new JournalLine { JournalEntryId = entry.Id, AccountCode = x.Account, Debit = x.Debit, Credit = x.Credit }).ToList();
        db.JournalEntries.Add(entry);
    }
    public static Task<Guid?> BranchFor(AppDbContext db, Guid warehouseId, CancellationToken ct) =>
        db.Branches.Where(x => x.WarehouseId == warehouseId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
    public static string Number(string prefix) => $"{prefix}-{DateTime.UtcNow:yyMMdd}-{Guid.NewGuid():N}"[..(prefix.Length + 20)].ToUpperInvariant();
    public static Task Lock(AppDbContext db, string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
    public static async Task LockProducts(AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    { foreach (var key in ids.Select(x => $"product:{x:N}").Distinct().Order(StringComparer.Ordinal)) await Lock(db, key, ct); }
    public static async Task LockWarehouses(AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    { foreach (var key in ids.Select(x => $"warehouse:{x:N}").Distinct().Order(StringComparer.Ordinal)) await Lock(db, key, ct); }
    public static async Task LockPositions(AppDbContext db, IEnumerable<(Guid ProductId, Guid WarehouseId)> positions, CancellationToken ct)
    { foreach (var key in positions.Select(x => $"stock:{x.ProductId:N}:{x.WarehouseId:N}").Distinct().Order(StringComparer.Ordinal)) await Lock(db, key, ct); }
    public static async Task LockUsers(AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    { foreach (var key in ids.Select(x => $"retail-user:{x:N}").Distinct().Order(StringComparer.Ordinal)) await Lock(db, key, ct); }
}
