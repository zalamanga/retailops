using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetailOps.Api.Auth;
using RetailOps.Api.Data;
using RetailOps.Api.Domain;
using RetailOps.Api.Security;

namespace RetailOps.Api.Services;

public sealed partial class RetailService(AppDbContext db, User actor)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Stamp(Guid Key, string Kind, string Hash);
    private static AppException Conflict(string message) => new(409, "Retail state changed", message);
    private static AppException Invalid(string message) => new(400, "Invalid retail request", message);
    private static AppException Missing(string name) => new(404, "Record not found", $"{name} was not found or is outside your access.");
    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static void Money(decimal value, bool positive = false)
    {
        if (value < (positive ? 0.01m : 0) || value > BusinessValidation.MoneyLimit || decimal.Round(value, 2) != value)
            throw Invalid("Use a bounded amount with at most two decimal places.");
    }
    private static string Text(string? value, int maximum)
    {
        var result = value?.Trim() ?? "";
        if (result.Length == 0 || result.Length > maximum || result.Any(char.IsControl)) throw Invalid("Provide the required text without control characters.");
        return result;
    }
    private async Task<User> Authorize(Guid? branchId, CancellationToken ct, bool manager = false, bool admin = false)
    {
        var live = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.Id && x.IsActive, ct)
            ?? throw new AppException(403, "Permission required", "Your account is no longer active.");
        if (admin ? live.Role != RoleNames.Admin : manager ? live.Role is not (RoleNames.Admin or RoleNames.Manager) : live.Role is not (RoleNames.Admin or RoleNames.Manager or RoleNames.Cashier))
            throw new AppException(403, "Permission required", "Your current role cannot perform this operation.");
        if (branchId.HasValue)
        {
            if (!await db.Branches.AnyAsync(x => x.Id == branchId, ct)) throw Missing("Branch");
            if (live.Role == RoleNames.Cashier && !await db.BranchAssignments.AnyAsync(x => x.UserId == live.Id && x.BranchId == branchId, ct))
                throw new AppException(403, "Branch access required", "You are not currently assigned to this branch.");
        }
        return live;
    }
    private Task ActorLock(CancellationToken ct) => Accounting.LockUsers(db, [actor.Id], ct);
    private async Task<(Stamp Stamp, JsonObject? Prior)> BeginKey(string? rawKey, string kind, object payload, bool replayFlag, CancellationToken ct)
    {
        if (!Guid.TryParse(rawKey, out var key) || key == Guid.Empty) throw Invalid("Send a non-empty UUID Idempotency-Key and retain it when retrying.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json))));
        var stamp = new Stamp(key, kind, hash);
        await Accounting.Lock(db, $"retail-key:{key:N}", ct);
        var prior = await db.RetailMutations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == key, ct);
        if (prior is null) return (stamp, null);
        if (prior.OwnerId != actor.Id || prior.Kind != kind || prior.Fingerprint != hash)
            throw Conflict("This idempotency key belongs to a different owner or command. Preserve the original key for uncertain retries.");
        var result = JsonNode.Parse(prior.ResponseJson)!.AsObject();
        if (replayFlag) result["replayed"] = true;
        return (stamp, result);
    }
    private async Task<JsonObject> Finish(Stamp stamp, object result, IDbContextTransaction transaction, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
        db.RetailMutations.Add(new RetailMutation { Id = stamp.Key, OwnerId = actor.Id, Kind = stamp.Kind, Fingerprint = stamp.Hash, ResponseJson = node.ToJsonString(Json) });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return node;
    }
    private void Audit(string action, Guid id, string description) => db.AuditEvents.Add(new AuditEvent
    { ActorId = actor.Id, ActorName = actor.FullName, Action = action, EntityType = "Retail", EntityId = id.ToString(), Description = description });
    private async Task<CashShift> OpenOwnShift(Guid id, Guid branchId, CancellationToken ct)
    {
        await Accounting.Lock(db, $"retail-shift:{id:N}", ct);
        var shift = await db.CashShifts.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing("Shift");
        if (shift.OpenedById != actor.Id) throw new AppException(403, "Own shift required", "Operate only your own shift.");
        if (shift.BranchId != branchId || shift.Status != "Open") throw Conflict("The selected shift is not open at this branch.");
        return shift;
    }
    private async Task<InventoryBalance> Balance(Guid productId, Guid warehouseId, CancellationToken ct)
    {
        var balance = await db.InventoryBalances.SingleOrDefaultAsync(x => x.ProductId == productId && x.WarehouseId == warehouseId, ct);
        if (balance is not null) return balance;
        balance = new InventoryBalance { ProductId = productId, WarehouseId = warehouseId };
        db.InventoryBalances.Add(balance); return balance;
    }
    private void Movement(Guid productId, Guid warehouseId, int quantity, string type, string number, Guid id) => db.StockMovements.Add(new StockMovement
    { ProductId = productId, WarehouseId = warehouseId, Quantity = quantity, Type = type, Reason = $"Retail {type.ToLowerInvariant()}.", Reference = number, ReferenceId = id, ActorId = actor.Id, ActorName = actor.FullName });
    private static object BranchView(Branch branch) => new { branch.Id, branch.Code, branch.Name, branch.WarehouseId, warehouseName = branch.Warehouse.Name };
    private static object ShiftView(CashShift shift, string branchName, decimal? currentExpected = null) => new
    { shift.Id, shift.BranchId, branchName, shift.OpenedById, shift.OpenedByName, shift.OpeningCash, shift.Status, shift.OpenedAt, shift.ClosedAt, shift.CountedCash,
        expectedCash = shift.Status == "Open" ? currentExpected ?? shift.OpeningCash : shift.ExpectedCash, shift.Variance };
    private async Task<object> SaleView(RetailSale sale, string branchName, bool replayed, CancellationToken ct)
    {
        var returned = await db.RetailReturnLines.AsNoTracking().Where(x => x.SaleLine.SaleId == sale.Id)
            .GroupBy(x => x.SaleLineId).Select(g => new { Id = g.Key, Quantity = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Id, x => x.Quantity, ct);
        return new { sale.Id, sale.Number, sale.BranchId, branchName, sale.ShiftId, sale.ActorId, sale.ActorName, sale.PriceTier, sale.Total,
            sale.CashTendered, sale.Change, sale.CreatedAt, sale.ClientCreatedAt,
            lines = sale.Lines.OrderBy(x => x.ProductId).Select(x => new { x.Id, x.ProductId, x.Sku, x.Name, x.Quantity, x.UnitPrice, x.LineTotal, returnedQuantity = returned.GetValueOrDefault(x.Id) }), replayed };
    }

    public async Task<object> CreateBranch(BranchWrite request, CancellationToken ct)
    {
        var code = Text(request.Code, 32).ToUpperInvariant(); var name = Text(request.Name, 120);
        if (code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) throw Invalid("Use a simple branch code.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await ActorLock(ct); await Authorize(null, ct, admin: true);
        await Accounting.LockWarehouses(db, [request.WarehouseId], ct);
        var warehouse = await db.Warehouses.SingleOrDefaultAsync(x => x.Id == request.WarehouseId, ct) ?? throw Missing("Warehouse");
        if (await db.InventoryBalances.AnyAsync(x => x.WarehouseId == warehouse.Id && x.OnHand > 0, ct)) throw Conflict("Map a branch only to an empty warehouse; the mapping is permanent.");
        var branch = new Branch { Code = code, Name = name, WarehouseId = warehouse.Id };
        db.Branches.Add(branch); Audit("Branch.Created", branch.Id, $"Branch {code} created."); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { branch.Id, branch.Code, branch.Name, branch.WarehouseId, warehouseName = warehouse.Name };
    }
    public async Task Assign(Guid userId, Guid branchId, bool remove, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Accounting.LockUsers(db, [actor.Id, userId], ct); await Authorize(null, ct, admin: true);
        if (!await db.Branches.AnyAsync(x => x.Id == branchId, ct) || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, ct)) throw Missing("User or branch");
        var assignment = await db.BranchAssignments.FindAsync([userId, branchId], ct);
        if (remove && assignment is not null) db.BranchAssignments.Remove(assignment);
        if (!remove && assignment is null) db.BranchAssignments.Add(new BranchAssignment { UserId = userId, BranchId = branchId });
        Audit(remove ? "BranchAssignment.Removed" : "BranchAssignment.Saved", branchId, $"Branch assignment for {userId} recorded.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<object> SetPrice(Guid id, PriceWrite request, CancellationToken ct)
    {
        Money(request.RetailPrice, true); Money(request.WholesalePrice, true);
        if (request.WholesalePrice > request.RetailPrice || request.WholesaleMinimum is < 2 or > BusinessValidation.QuantityLimit || request.ExpectedVersion < 0) throw Invalid("Wholesale price must not exceed retail; the minimum must be at least two.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await ActorLock(ct); await Authorize(null, ct, admin: true); await Accounting.LockProducts(db, [id], ct);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing("Product");
        var price = await db.ProductPrices.SingleOrDefaultAsync(x => x.ProductId == id, ct);
        if ((price?.Version ?? 0) != request.ExpectedVersion) throw Conflict("The price version changed. Reload the current price.");
        if (price is null) { price = new ProductPrice { ProductId = id }; db.ProductPrices.Add(price); }
        price.RetailPrice = request.RetailPrice; price.WholesalePrice = request.WholesalePrice; price.WholesaleMinimum = request.WholesaleMinimum;
        if (price.Version == long.MaxValue) throw Conflict("The price version limit was reached."); price.Version++;
        Audit("Price.Saved", id, "Retail and wholesale prices saved."); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new { productId = id, product.Sku, product.Name, price.RetailPrice, price.WholesalePrice, price.WholesaleMinimum, price.Version };
    }
    public async Task<JsonObject> OpenShift(string? key, ShiftWrite request, CancellationToken ct)
    {
        Money(request.OpeningCash);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await ActorLock(ct); var live = await Authorize(request.BranchId, ct);
        var (stamp, prior) = await BeginKey(key, "OpenShift", new { request.BranchId, openingCash = Amount(request.OpeningCash) }, false, ct);
        if (prior is not null) return prior;
        if (await db.CashShifts.AnyAsync(x => x.OpenedById == actor.Id && x.Status == "Open", ct)) throw Conflict("Close your existing shift before opening another.");
        var branch = await db.Branches.AsNoTracking().SingleAsync(x => x.Id == request.BranchId, ct);
        var shift = new CashShift { BranchId = branch.Id, OpenedById = actor.Id, OpenedByName = live.FullName, OpeningCash = request.OpeningCash };
        db.CashShifts.Add(shift); Audit("Shift.Opened", shift.Id, "Cash drawer opened; opening cash is an operational count, not an equity posting.");
        return await Finish(stamp, ShiftView(shift, branch.Name), tx, ct);
    }
    public async Task<JsonObject> CloseShift(Guid id, string? key, CloseShiftWrite request, CancellationToken ct)
    {
        Money(request.CountedCash);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await ActorLock(ct);
        var identity = await db.CashShifts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing("Shift");
        await Authorize(identity.BranchId, ct);
        if (identity.OpenedById != actor.Id) throw new AppException(403, "Own shift required", "Close only your own shift.");
        var (stamp, prior) = await BeginKey(key, "CloseShift", new { shiftId = id, countedCash = Amount(request.CountedCash) }, false, ct);
        if (prior is not null) return prior;
        var shift = await OpenOwnShift(id, identity.BranchId, ct);
        var sales = await db.RetailSales.Where(x => x.ShiftId == id).SumAsync(x => (decimal?)x.Total, ct) ?? 0;
        var returns = await db.RetailReturns.Where(x => x.ShiftId == id).SumAsync(x => (decimal?)x.Total, ct) ?? 0;
        shift.ExpectedCash = shift.OpeningCash + sales - returns; shift.CountedCash = request.CountedCash; shift.Variance = request.CountedCash - shift.ExpectedCash;
        shift.Status = "Closed"; shift.ClosedAt = DateTimeOffset.UtcNow;
        var name = await db.Branches.Where(x => x.Id == shift.BranchId).Select(x => x.Name).SingleAsync(ct);
        Audit("Shift.Closed", id, "Cash drawer counted; variance is an operational count without automatic GL posting.");
        return await Finish(stamp, ShiftView(shift, name), tx, ct);
    }
    public async Task<JsonObject> Sell(string? key, SaleWrite request, CancellationToken ct)
    {
        Money(request.CashTendered);
        if (request.PriceTier is not ("Retail" or "Wholesale") || request.ClientCreatedAt < DateTimeOffset.UnixEpoch || request.ClientCreatedAt > DateTimeOffset.UtcNow.AddDays(1)) throw Invalid("Choose a price tier and provide a valid ISO client time.");
        if (request.Lines is null || request.Lines.Count is < 1 or > 50 || request.Lines.Any(x => x is null) || request.Lines.Select(x => x.ProductId).Distinct().Count() != request.Lines.Count) throw Invalid("A sale requires one to fifty distinct product lines.");
        foreach (var line in request.Lines) { Money(line.UnitPrice, true); if (line.ProductId == Guid.Empty || line.Quantity is < 1 or > BusinessValidation.QuantityLimit || line.PriceVersion < 1) throw Invalid("Provide a product, positive quantity and current price version."); }
        var lines = request.Lines.OrderBy(x => x.ProductId).ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct); await ActorLock(ct); var live = await Authorize(request.BranchId, ct);
        var (stamp, prior) = await BeginKey(key, "Sale", new { request.BranchId, request.ShiftId, request.PriceTier, clientCreatedAt = request.ClientCreatedAt.ToUniversalTime().ToString("O"), cashTendered = Amount(request.CashTendered), lines = lines.Select(x => new { x.ProductId, x.Quantity, unitPrice = Amount(x.UnitPrice), x.PriceVersion }) }, true, ct);
        if (prior is not null) return prior;
        await OpenOwnShift(request.ShiftId, request.BranchId, ct);
        await Accounting.LockProducts(db, lines.Select(x => x.ProductId), ct);
        var branch = await db.Branches.AsNoTracking().SingleAsync(x => x.Id == request.BranchId, ct);
        await Accounting.LockWarehouses(db, [branch.WarehouseId], ct);
        await Accounting.LockPositions(db, lines.Select(x => (x.ProductId, branch.WarehouseId)), ct);
        var ids = lines.Select(x => x.ProductId).ToArray();
        var prices = await db.ProductPrices.Include(x => x.Product).Where(x => ids.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, ct);
        var sale = new RetailSale { Number = Accounting.Number("SALE"), BranchId = branch.Id, ShiftId = request.ShiftId, ActorId = actor.Id, ActorName = live.FullName, PriceTier = request.PriceTier, ClientCreatedAt = request.ClientCreatedAt.ToUniversalTime(), CashTendered = request.CashTendered };
        foreach (var line in lines)
        {
            if (!prices.TryGetValue(line.ProductId, out var price) || !price.Product.IsActive) throw Conflict("A product is inactive or has no current price.");
            var expected = request.PriceTier == "Retail" ? price.RetailPrice : price.WholesalePrice;
            if (price.Version != line.PriceVersion || expected != line.UnitPrice || request.PriceTier == "Wholesale" && line.Quantity < price.WholesaleMinimum) throw Conflict("Cached prices or wholesale quantities no longer match. Review the current catalog.");
            var total = line.Quantity * expected; Money(total, true);
            var balance = await Balance(line.ProductId, branch.WarehouseId, ct);
            if (balance.OnHand < line.Quantity) throw Conflict($"Insufficient stock for {price.Product.Sku}. Refresh the catalog.");
            balance.OnHand -= line.Quantity;
            sale.Lines.Add(new RetailSaleLine { SaleId = sale.Id, ProductId = line.ProductId, Sku = price.Product.Sku, Name = price.Product.Name, Quantity = line.Quantity, UnitPrice = expected, StandardCost = price.Product.StandardCost, LineTotal = total });
            Movement(line.ProductId, branch.WarehouseId, -line.Quantity, "Sale", sale.Number, sale.Id);
        }
        sale.Total = sale.Lines.Sum(x => x.LineTotal); Money(sale.Total, true);
        if (sale.CashTendered < sale.Total) throw Invalid("Cash tendered must cover the accepted sale total."); sale.Change = sale.CashTendered - sale.Total;
        db.RetailSales.Add(sale); var cost = sale.Lines.Sum(x => x.Quantity * x.StandardCost);
        Accounting.PostAt(db, "Sale", sale.Id, branch.Id, $"Sale {sale.Number}", sale.CreatedAt, ("1000", sale.Total, 0), ("4000", 0, sale.Total), ("5000", cost, 0), ("1200", 0, cost));
        Audit("Sale.Accepted", sale.Id, $"{sale.Number} accepted atomically with stock and GL.");
        return await Finish(stamp, await SaleView(sale, branch.Name, false, ct), tx, ct);
    }
    public async Task<JsonObject> Return(Guid saleId, string? key, ReturnWrite request, CancellationToken ct)
    {
        var reason = Text(request.Reason, 1000);
        if (request.Lines is null || request.Lines.Count is < 1 or > 50 || request.Lines.Any(x => x is null) || request.Lines.Select(x => x.SaleLineId).Distinct().Count() != request.Lines.Count || request.Lines.Any(x => x.SaleLineId == Guid.Empty || x.Quantity is < 1 or > BusinessValidation.QuantityLimit)) throw Invalid("A return requires distinct sale lines and positive quantities.");
        var lines = request.Lines.OrderBy(x => x.SaleLineId).ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct); await ActorLock(ct); await Authorize(null, ct, manager: true);
        var sale = await db.RetailSales.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == saleId, ct) ?? throw Missing("Sale");
        await Authorize(sale.BranchId, ct, manager: true);
        var (stamp, prior) = await BeginKey(key, "Return", new { saleId, request.ShiftId, reason, lines = lines.Select(x => new { x.SaleLineId, x.Quantity }) }, true, ct);
        if (prior is not null) return prior;
        await OpenOwnShift(request.ShiftId, sale.BranchId, ct); await Accounting.Lock(db, $"retail-sale:{saleId:N}", ct);
        var originals = sale.Lines.ToDictionary(x => x.Id);
        if (lines.Any(x => !originals.ContainsKey(x.SaleLineId))) throw Invalid("Every return line must belong to this sale.");
        var returned = await db.RetailReturnLines.Where(x => x.SaleLine.SaleId == saleId).GroupBy(x => x.SaleLineId).Select(g => new { Id = g.Key, Quantity = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Id, x => x.Quantity, ct);
        if (lines.Any(x => (long)returned.GetValueOrDefault(x.SaleLineId) + x.Quantity > originals[x.SaleLineId].Quantity)) throw Conflict("The cumulative return quantity exceeds the original sale.");
        await Accounting.LockProducts(db, lines.Select(x => originals[x.SaleLineId].ProductId), ct);
        var branch = await db.Branches.AsNoTracking().SingleAsync(x => x.Id == sale.BranchId, ct);
        await Accounting.LockWarehouses(db, [branch.WarehouseId], ct); await Accounting.LockPositions(db, lines.Select(x => (originals[x.SaleLineId].ProductId, branch.WarehouseId)), ct);
        var result = new RetailReturn { Number = Accounting.Number("RET"), SaleId = sale.Id, ShiftId = request.ShiftId, BranchId = branch.Id, ActorId = actor.Id, Reason = reason };
        foreach (var line in lines)
        {
            var original = originals[line.SaleLineId]; var balance = await Balance(original.ProductId, branch.WarehouseId, ct);
            if (balance.OnHand > 9_007_199_254_740_991L - line.Quantity) throw Conflict("The stock balance limit was reached.");
            balance.OnHand += line.Quantity;
            result.Lines.Add(new RetailReturnLine { ReturnId = result.Id, SaleLineId = original.Id, Quantity = line.Quantity });
            Movement(original.ProductId, branch.WarehouseId, line.Quantity, "Return", result.Number, result.Id);
        }
        result.Total = lines.Sum(x => x.Quantity * originals[x.SaleLineId].UnitPrice); Money(result.Total, true);
        var cost = lines.Sum(x => x.Quantity * originals[x.SaleLineId].StandardCost);
        var productIds = lines.Select(x => originals[x.SaleLineId].ProductId).ToArray();
        var currentCosts = await db.Products.Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.StandardCost, ct);
        var inventoryValue = lines.Sum(x => x.Quantity * currentCosts[originals[x.SaleLineId].ProductId]);
        var valuationVariance = cost - inventoryValue;
        db.RetailReturns.Add(result);
        Accounting.PostAt(db, "Return", result.Id, branch.Id, $"Return {result.Number}; original COGS reversed, current-standard valuation variance recorded.", result.CreatedAt,
            ("4000", result.Total, 0), ("1000", 0, result.Total), ("1200", inventoryValue, 0), ("5000", 0, cost),
            ("5100", Math.Max(valuationVariance, 0), Math.Max(-valuationVariance, 0)));
        Audit("Return.Accepted", result.Id, reason);
        return await Finish(stamp, new { result.Id, result.Number, result.SaleId, result.ShiftId, result.Total, result.CreatedAt, replayed = false }, tx, ct);
    }
}
