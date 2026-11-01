using System.Globalization;
using System.Data;
using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Auth;
using RetailOps.Api.Domain;

namespace RetailOps.Api.Services;

public sealed partial class RetailService
{
    public async Task<object> Branches(CancellationToken ct)
    {
        var live = await Authorize(null, ct);
        var query = db.Branches.AsNoTracking().Include(x => x.Warehouse).AsQueryable();
        if (live.Role == RoleNames.Cashier) query = query.Where(x => db.BranchAssignments.Any(a => a.UserId == actor.Id && a.BranchId == x.Id));
        return (await query.OrderBy(x => x.Code).Take(200).ToListAsync(ct)).Select(BranchView).ToArray();
    }
    public async Task<object> Assignments(CancellationToken ct)
    {
        await Authorize(null, ct, admin: true);
        return await db.BranchAssignments.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.BranchId).Take(200).Select(x => new { x.UserId, x.BranchId }).ToListAsync(ct);
    }
    public async Task<object> Prices(CancellationToken ct)
    {
        await Authorize(null, ct, manager: true);
        return await db.Products.AsNoTracking().OrderBy(x => x.Sku).Take(200).Select(x => new
        {
            productId = x.Id, x.Sku, x.Name,
            retailPrice = db.ProductPrices.Where(p => p.ProductId == x.Id).Select(p => (decimal?)p.RetailPrice).FirstOrDefault() ?? 0,
            wholesalePrice = db.ProductPrices.Where(p => p.ProductId == x.Id).Select(p => (decimal?)p.WholesalePrice).FirstOrDefault() ?? 0,
            wholesaleMinimum = db.ProductPrices.Where(p => p.ProductId == x.Id).Select(p => (int?)p.WholesaleMinimum).FirstOrDefault() ?? 2,
            version = db.ProductPrices.Where(p => p.ProductId == x.Id).Select(p => (long?)p.Version).FirstOrDefault() ?? 0
        }).ToListAsync(ct);
    }
    public async Task<object> Catalog(Guid branchId, CancellationToken ct)
    {
        await Authorize(branchId, ct);
        var branch = await db.Branches.AsNoTracking().Include(x => x.Warehouse).SingleAsync(x => x.Id == branchId, ct);
        var products = await db.ProductPrices.AsNoTracking().Where(x => x.Product.IsActive).OrderBy(x => x.Product.Sku).Take(200)
            .Select(x => new { x.ProductId, x.Product.Sku, x.Product.Name, x.Product.Unit,
                onHand = db.InventoryBalances.Where(b => b.ProductId == x.ProductId && b.WarehouseId == branch.WarehouseId).Select(b => (long?)b.OnHand).FirstOrDefault() ?? 0,
                x.RetailPrice, x.WholesalePrice, x.WholesaleMinimum, x.Version }).ToListAsync(ct);
        return new { branch = BranchView(branch), products, capturedAt = DateTimeOffset.UtcNow };
    }
    public async Task<object> Shifts(Guid? branchId, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var live = await Authorize(branchId, ct);
        var query = db.CashShifts.AsNoTracking().Include(x => x.Branch).AsQueryable();
        if (branchId.HasValue) query = query.Where(x => x.BranchId == branchId);
        if (live.Role == RoleNames.Cashier) query = query.Where(x => x.OpenedById == actor.Id && db.BranchAssignments.Any(a => a.UserId == actor.Id && a.BranchId == x.BranchId));
        var shifts = await query.OrderByDescending(x => x.OpenedAt).ThenBy(x => x.Id).Take(200).ToListAsync(ct);
        var openIds = shifts.Where(x => x.Status == "Open").Select(x => x.Id).ToArray();
        var sales = await db.RetailSales.AsNoTracking().Where(x => openIds.Contains(x.ShiftId)).GroupBy(x => x.ShiftId)
            .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Total) }).ToDictionaryAsync(x => x.Id, x => x.Total, ct);
        var refunds = await db.RetailReturns.AsNoTracking().Where(x => openIds.Contains(x.ShiftId)).GroupBy(x => x.ShiftId)
            .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Total) }).ToDictionaryAsync(x => x.Id, x => x.Total, ct);
        var result = shifts.Select(x => ShiftView(x, x.Branch.Name, x.OpeningCash + sales.GetValueOrDefault(x.Id) - refunds.GetValueOrDefault(x.Id))).ToArray();
        await snapshot.CommitAsync(ct);
        return result;
    }
    public async Task<object> Sales(Guid? branchId, CancellationToken ct)
    {
        var live = await Authorize(branchId, ct);
        var query = db.RetailSales.AsNoTracking().AsQueryable();
        if (branchId.HasValue) query = query.Where(x => x.BranchId == branchId);
        if (live.Role == RoleNames.Cashier) query = query.Where(x => x.ActorId == actor.Id && db.BranchAssignments.Any(a => a.UserId == actor.Id && a.BranchId == x.BranchId));
        return await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(200)
            .Select(x => new { x.Id, x.Number, x.BranchId, branchName = x.Branch.Name, x.ShiftId, x.ActorId, x.ActorName, x.PriceTier, x.Total, x.CreatedAt }).ToListAsync(ct);
    }
    public async Task<object> Sale(Guid id, CancellationToken ct)
    {
        var live = await Authorize(null, ct);
        var sale = await db.RetailSales.AsNoTracking().Include(x => x.Branch).Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing("Sale");
        await Authorize(sale.BranchId, ct);
        if (live.Role == RoleNames.Cashier && sale.ActorId != actor.Id) throw Missing("Sale");
        return await SaleView(sale, sale.Branch.Name, false, ct);
    }
    public async Task<object> Journals(CancellationToken ct)
    {
        await Authorize(null, ct, manager: true);
        var entries = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).ThenInclude(x => x.Account)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(200).ToListAsync(ct);
        return entries.Select(x => new { x.Id, x.Number, x.SourceType, x.SourceId, x.BranchId, x.Description, x.CreatedAt,
            totalDebit = x.Lines.Sum(l => l.Debit), totalCredit = x.Lines.Sum(l => l.Credit),
            lines = x.Lines.OrderBy(l => l.AccountCode).ThenBy(l => l.Id).Select(l => new { l.AccountCode, accountName = l.Account.Name, l.Debit, l.Credit }) }).ToArray();
    }
    public async Task<object> Reports(string? from, string? to, Guid? branchId, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Authorize(branchId, ct, manager: true);
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate)
            || endDate < startDate || endDate.DayNumber - startDate.DayNumber > 365 || startDate.Year < 1970 || endDate.Year > 9998)
            throw Invalid("Use an ordered YYYY-MM-DD range of at most 366 inclusive WIB dates.");
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).ToUniversalTime();
        var end = new DateTimeOffset(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).ToUniversalTime();
        var sales = db.RetailSales.AsNoTracking().Where(x => x.CreatedAt >= start && x.CreatedAt < end);
        var returns = db.RetailReturns.AsNoTracking().Where(x => x.CreatedAt >= start && x.CreatedAt < end);
        var period = db.JournalLines.AsNoTracking().Where(x => x.JournalEntry.CreatedAt >= start && x.JournalEntry.CreatedAt < end);
        if (branchId.HasValue) { sales = sales.Where(x => x.BranchId == branchId); returns = returns.Where(x => x.BranchId == branchId); period = period.Where(x => x.JournalEntry.BranchId == branchId); }
        var salesCount = await sales.CountAsync(ct); var grossSales = await sales.SumAsync(x => (decimal?)x.Total, ct) ?? 0;
        var refunds = await returns.SumAsync(x => (decimal?)x.Total, ct) ?? 0;
        var expenses = await period.GroupBy(x => x.AccountCode).Select(g => new { Code = g.Key, Value = g.Sum(x => x.Debit - x.Credit) }).ToDictionaryAsync(x => x.Code, x => x.Value, ct);
        var totals = await db.JournalLines.AsNoTracking().Where(x => x.JournalEntry.CreatedAt < end).GroupBy(x => x.AccountCode)
            .Select(g => new { Code = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToDictionaryAsync(x => x.Code, ct);
        var accounts = await db.FinancialAccounts.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct);
        decimal Debit(string code) => totals.TryGetValue(code, out var value) ? value.Debit : 0;
        decimal Credit(string code) => totals.TryGetValue(code, out var value) ? value.Credit : 0;
        var retained = accounts.Where(x => x.Type == "Revenue").Sum(x => Credit(x.Code) - Debit(x.Code)) - accounts.Where(x => x.Type == "Expense").Sum(x => Debit(x.Code) - Credit(x.Code));
        var assets = accounts.Where(x => x.Type == "Asset").Sum(x => Debit(x.Code) - Credit(x.Code));
        var liabilities = accounts.Where(x => x.Type == "Liability").Sum(x => Credit(x.Code) - Debit(x.Code));
        var equity = accounts.Where(x => x.Type == "Equity").Sum(x => Credit(x.Code) - Debit(x.Code)) + retained;
        var cogs = expenses.GetValueOrDefault("5000"); var variance = expenses.GetValueOrDefault("5100");
        var result = new { from, to, branchId, salesCount, grossSales, refunds, netSales = grossSales - refunds, costOfGoodsSold = cogs,
            varianceExpense = variance, netProfit = grossSales - refunds - cogs - variance,
            trialBalance = accounts.Select(x => new { x.Code, x.Name, x.Type, debit = Debit(x.Code), credit = Credit(x.Code), balance = x.Type is "Asset" or "Expense" ? Debit(x.Code) - Credit(x.Code) : Credit(x.Code) - Debit(x.Code) }),
            balanceSheet = new { asOf = to, assets, liabilities, equity, retainedEarnings = retained, balanced = assets == liabilities + equity },
            journalBalanced = totals.Values.Sum(x => x.Debit) == totals.Values.Sum(x => x.Credit) };
        await snapshot.CommitAsync(ct);
        return result;
    }
}
