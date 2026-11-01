using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Data;
using RetailOps.Api.Domain;
using RetailOps.Api.Security;

namespace RetailOps.Api.Services;

public sealed class BusinessQueries(AppDbContext db)
{
    public async Task<PageResult<ProductView>> Products(HttpRequest request, CancellationToken ct)
    {
        var (page, size) = BusinessValidation.Pagination(request);
        var query = db.Products.AsNoTracking().AsQueryable();
        if (!BusinessValidation.BooleanQuery(request, "includeArchived")) query = query.Where(x => x.IsActive);
        var search = BusinessValidation.Search(request);
        if (search.Length > 0)
        {
            var like = BusinessValidation.Like(search);
            query = query.Where(x => EF.Functions.ILike(x.Name, like) || EF.Functions.ILike(x.Sku, like));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Sku).ThenBy(x => x.Id).Skip(BusinessValidation.Skip(page, size)).Take(size)
            .Select(x => new ProductView(x.Id, x.Sku, x.Name, x.Unit, x.ReorderPoint, x.StandardCost, x.IsActive)).ToListAsync(ct);
        return new(items, total, page, size);
    }

    public async Task<PageResult<SupplierView>> Suppliers(HttpRequest request, CancellationToken ct)
    {
        var (page, size) = BusinessValidation.Pagination(request);
        var query = db.Suppliers.AsNoTracking().AsQueryable();
        var search = BusinessValidation.Search(request);
        if (search.Length > 0)
        {
            var like = BusinessValidation.Like(search);
            query = query.Where(x => EF.Functions.ILike(x.Name, like) || EF.Functions.ILike(x.Email, like));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(BusinessValidation.Skip(page, size)).Take(size)
            .Select(x => new SupplierView(x.Id, x.Name, x.Email, x.Phone, x.LeadTimeDays)).ToListAsync(ct);
        return new(items, total, page, size);
    }

    public Task<List<WarehouseView>> Warehouses(CancellationToken ct) => db.Warehouses.AsNoTracking().OrderBy(x => x.Code)
        .Select(x => new WarehouseView(x.Id, x.Code, x.Name)).ToListAsync(ct);

    public async Task<PageResult<OrderSummaryView>> Orders(HttpRequest request, CancellationToken ct)
    {
        var (page, size) = BusinessValidation.Pagination(request);
        var query = db.Orders.AsNoTracking().AsQueryable();
        var status = request.Query["status"].ToString();
        if (status.Length > 0)
        {
            if (!OrderStatuses.All.Contains(status)) throw new AppException(400, "Invalid request", "Choose a valid order status.");
            query = query.Where(x => x.Status == status);
        }
        var search = BusinessValidation.Search(request);
        if (search.Length > 0)
        {
            var like = BusinessValidation.Like(search);
            query = query.Where(x => EF.Functions.ILike(x.Number, like) || EF.Functions.ILike(x.Supplier.Name, like)
                || EF.Functions.ILike(x.Warehouse.Name, like));
        }
        var total = await query.CountAsync(ct);
        var items = await Summaries(query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(BusinessValidation.Skip(page, size)).Take(size)).ToListAsync(ct);
        return new(items, total, page, size);
    }

    public async Task<OrderDetailView> Order(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Supplier).Include(x => x.Warehouse)
            .Include(x => x.Lines).Include(x => x.Receipts).ThenInclude(x => x.Lines).AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing("Purchase order");
        return new(order.Id, order.Number, order.Supplier.Name, order.Warehouse.Name, order.Status,
            order.Lines.Sum(x => x.Quantity * x.UnitCost), order.ExpectedDate, order.CreatedAt, order.CreatedByName,
            order.SupplierId, order.WarehouseId, order.CreatedById, order.Notes, order.DecisionReason,
            order.Lines.OrderBy(x => x.Sku).ThenBy(x => x.Id).Select(x => new OrderLineView(x.Id, x.ProductId, x.Sku,
                x.ProductName, x.Quantity, x.ReceivedQuantity, x.UnitCost, x.Quantity * x.UnitCost)).ToList(),
            order.Receipts.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => new ReceiptView(x.Id, x.Number,
                x.CreatedAt, x.CreatedByName, x.Notes, x.Lines.OrderBy(l => l.OrderLineId)
                    .Select(l => new ReceiptLineView(l.OrderLineId, l.Quantity)).ToList())).ToList());
    }

    public async Task<PageResult<InventoryView>> Inventory(HttpRequest request, CancellationToken ct)
    {
        var (page, size) = BusinessValidation.Pagination(request);
        var query = InventoryQuery(request);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Sku).ThenBy(x => x.WarehouseName).ThenBy(x => x.WarehouseId)
            .Skip(BusinessValidation.Skip(page, size)).Take(size).ToListAsync(ct);
        return new(items, total, page, size);
    }

    public async Task<byte[]> InventoryCsv(HttpRequest request, CancellationToken ct)
    {
        var text = new StringBuilder("SKU,Product,Unit,Warehouse,On hand,Reorder point,Standard cost,Stock value,Low stock\r\n");
        await foreach (var row in InventoryQuery(request).OrderBy(x => x.Sku).ThenBy(x => x.WarehouseName)
            .ThenBy(x => x.WarehouseId).AsAsyncEnumerable().WithCancellation(ct))
        {
            text.Append(string.Join(',', CsvText(row.Sku), CsvText(row.ProductName), CsvText(row.Unit), CsvText(row.WarehouseName),
                row.OnHand.ToString(CultureInfo.InvariantCulture), row.ReorderPoint.ToString(CultureInfo.InvariantCulture),
                row.StandardCost.ToString("0.00", CultureInfo.InvariantCulture), row.StockValue.ToString("0.00", CultureInfo.InvariantCulture),
                row.IsLowStock ? "Yes" : "No")).Append("\r\n");
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray();
    }

    // A quoted CSV field can still execute as a spreadsheet formula. Prefix user text before escaping quotes.
    public static string CsvText(string value)
    {
        var trimmed = value.TrimStart();
        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || value[0] is '\t' or '\r' or '\n'
            || trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public async Task<PageResult<MovementView>> Movements(HttpRequest request, CancellationToken ct)
    {
        var (page, size) = BusinessValidation.Pagination(request);
        var query = db.StockMovements.AsNoTracking().AsQueryable();
        var productId = BusinessValidation.GuidQuery(request, "productId");
        var warehouseId = BusinessValidation.GuidQuery(request, "warehouseId");
        if (productId.HasValue) query = query.Where(x => x.ProductId == productId);
        if (warehouseId.HasValue) query = query.Where(x => x.WarehouseId == warehouseId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(BusinessValidation.Skip(page, size)).Take(size).Select(x => new MovementView(x.Id, x.Product.Sku,
                x.Product.Name, x.Warehouse.Name, x.Quantity, x.Type, x.Reason, x.Reference, x.CreatedAt, x.ActorName)).ToListAsync(ct);
        return new(items, total, page, size);
    }

    public async Task<PageResult<ActivityView>> Activity(HttpRequest request, CancellationToken ct)
    {
        var (page, size) = BusinessValidation.Pagination(request);
        var query = db.AuditEvents.AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(BusinessValidation.Skip(page, size)).Take(size)
            .Select(x => new ActivityView(x.Id, x.ActorName, x.Action, x.EntityType, x.EntityId, x.Description, x.CreatedAt)).ToListAsync(ct);
        return new(items, total, page, size);
    }

    public async Task<DashboardView> Dashboard(CancellationToken ct)
    {
        var activeProducts = await db.Products.CountAsync(x => x.IsActive, ct);
        var positions = BaseInventoryQuery();
        var stockValue = await positions.SumAsync(x => (decimal?)x.StockValue, ct) ?? 0m;
        var lowStockCount = await positions.CountAsync(x => x.IsLowStock, ct);
        var lowStock = await positions.Where(x => x.IsLowStock).OrderBy(x => x.OnHand).ThenBy(x => x.Sku)
            .ThenBy(x => x.WarehouseId).Take(6).ToListAsync(ct);
        var openOrders = await db.Orders.CountAsync(x => x.Status == OrderStatuses.Draft || x.Status == OrderStatuses.PendingApproval
            || x.Status == OrderStatuses.Approved || x.Status == OrderStatuses.PartiallyReceived, ct);
        var recentOrders = await Summaries(db.Orders.AsNoTracking().OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(5)).ToListAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = new DateTimeOffset(today.AddDays(-6).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var totals = await db.StockMovements.AsNoTracking().Where(x => x.CreatedAt >= start && x.CreatedAt < end)
            .GroupBy(x => DateOnly.FromDateTime(x.CreatedAt.UtcDateTime))
            .Select(g => new { Date = g.Key, Incoming = g.Sum(x => x.Quantity > 0 ? x.Quantity : 0), Outgoing = g.Sum(x => x.Quantity < 0 ? -x.Quantity : 0) })
            .ToListAsync(ct);
        var days = Enumerable.Range(-6, 7).Select(offset =>
        {
            var date = today.AddDays(offset);
            var total = totals.SingleOrDefault(x => x.Date == date);
            return new MovementDayView(date, total?.Incoming ?? 0, total?.Outgoing ?? 0);
        }).ToList();
        return new(activeProducts, stockValue, lowStockCount, openOrders, days, lowStock, recentOrders);
    }

    private IQueryable<InventoryView> InventoryQuery(HttpRequest request)
    {
        var query = BaseInventoryQuery();
        var warehouseId = BusinessValidation.GuidQuery(request, "warehouseId");
        if (warehouseId.HasValue) query = query.Where(x => x.WarehouseId == warehouseId);
        if (BusinessValidation.BooleanQuery(request, "lowStockOnly")) query = query.Where(x => x.IsLowStock);
        var search = BusinessValidation.Search(request);
        if (search.Length > 0)
        {
            var like = BusinessValidation.Like(search);
            query = query.Where(x => EF.Functions.ILike(x.Sku, like) || EF.Functions.ILike(x.ProductName, like)
                || EF.Functions.ILike(x.WarehouseName, like));
        }
        return query;
    }

    private IQueryable<InventoryView> BaseInventoryQuery() =>
        from product in db.Products.AsNoTracking()
        from warehouse in db.Warehouses.AsNoTracking()
        join balance in db.InventoryBalances.AsNoTracking() on new { ProductId = product.Id, WarehouseId = warehouse.Id }
            equals new { balance.ProductId, balance.WarehouseId } into balances
        from balance in balances.DefaultIfEmpty()
        where product.IsActive || (balance != null && balance.OnHand > 0)
        select new InventoryView
        {
            ProductId = product.Id,
            WarehouseId = warehouse.Id,
            Sku = product.Sku,
            ProductName = product.Name,
            Unit = product.Unit,
            WarehouseName = warehouse.Name,
            OnHand = balance == null ? 0L : balance.OnHand,
            ReorderPoint = product.ReorderPoint,
            StandardCost = product.StandardCost,
            StockValue = (balance == null ? 0L : balance.OnHand) * product.StandardCost,
            IsLowStock = product.IsActive && (balance == null ? 0L : balance.OnHand) <= product.ReorderPoint
        };

    private static IQueryable<OrderSummaryView> Summaries(IQueryable<PurchaseOrder> query) => query.Select(x => new OrderSummaryView(
        x.Id, x.Number, x.Supplier.Name, x.Warehouse.Name, x.Status, x.Lines.Sum(l => l.Quantity * l.UnitCost),
        x.ExpectedDate, x.CreatedAt, x.CreatedByName));

    internal static AppException Missing(string name) => new(404, "Record not found", $"{name} was not found. Refresh the page and choose an existing record.");
}
