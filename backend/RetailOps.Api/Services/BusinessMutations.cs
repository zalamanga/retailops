using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RetailOps.Api.Auth;
using RetailOps.Api.Data;
using RetailOps.Api.Domain;
using RetailOps.Api.Security;

namespace RetailOps.Api.Services;

public sealed partial class BusinessMutations(AppDbContext db, User actor)
{
    public async Task<ProductView> SaveProduct(Guid? id, ProductWrite request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var sku = BusinessValidation.Text("sku", request.Sku, 64, errors).ToUpperInvariant();
        var name = BusinessValidation.Text("name", request.Name, 180, errors);
        var unit = BusinessValidation.Text("unit", request.Unit, 24, errors);
        if (sku.Length > 0 && !SkuPattern().IsMatch(sku)) errors["sku"] = ["Use letters, numbers, dots, hyphens or underscores; start with a letter or number."];
        if (request.ReorderPoint < 0 || request.ReorderPoint > BusinessValidation.QuantityLimit)
            errors["reorderPoint"] = ["Enter a nonnegative whole quantity up to 1000000000."];
        BusinessValidation.Money("standardCost", request.StandardCost, errors);
        BusinessValidation.ThrowIfInvalid(errors);
        var productId = id ?? Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockProducts([productId], ct);
        var product = id.HasValue ? await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw BusinessQueries.Missing("Product") : new Product { Id = productId };
        if (id.HasValue && product.StandardCost != request.StandardCost && await db.InventoryBalances.AnyAsync(x => x.ProductId == productId && x.OnHand > 0, ct))
            throw new AppException(409, "Stock valuation is fixed", "Standard cost cannot change while stock remains. Clear stock through documented movements first.");
        product.Sku = sku;
        product.Name = name;
        product.Unit = unit;
        product.ReorderPoint = request.ReorderPoint;
        product.StandardCost = request.StandardCost;
        product.IsActive = request.IsActive;
        if (!id.HasValue) db.Products.Add(product);
        Audit(id.HasValue ? "Product.Updated" : "Product.Created", "Product", product.Id, $"{sku}: {name}{(product.IsActive ? "" : " (archived)")}.");
        await Save(ct, "A product with this SKU already exists. Use another SKU or update the existing product.");
        await transaction.CommitAsync(ct);
        return new(product.Id, product.Sku, product.Name, product.Unit, product.ReorderPoint, product.StandardCost, product.IsActive);
    }

    public async Task<SupplierView> SaveSupplier(Guid? id, SupplierWrite request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = BusinessValidation.Text("name", request.Name, 180, errors);
        var email = BusinessValidation.Text("email", request.Email, 254, errors, false);
        var phone = BusinessValidation.Text("phone", request.Phone, 40, errors, false);
        BusinessValidation.Email(email, errors);
        if (request.LeadTimeDays < 0 || request.LeadTimeDays > 3650) errors["leadTimeDays"] = ["Enter a whole number from 0 to 3650 days."];
        BusinessValidation.ThrowIfInvalid(errors);
        var supplier = id.HasValue ? await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw BusinessQueries.Missing("Supplier") : new Supplier();
        supplier.Name = name;
        supplier.Email = email;
        supplier.Phone = phone;
        supplier.LeadTimeDays = request.LeadTimeDays;
        if (!id.HasValue) db.Suppliers.Add(supplier);
        Audit(id.HasValue ? "Supplier.Updated" : "Supplier.Created", "Supplier", supplier.Id, $"Supplier {name} saved.");
        await Save(ct);
        return new(supplier.Id, supplier.Name, supplier.Email, supplier.Phone, supplier.LeadTimeDays);
    }

    public async Task<WarehouseView> CreateWarehouse(WarehouseWrite request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var code = BusinessValidation.Text("code", request.Code, 32, errors).ToUpperInvariant();
        var name = BusinessValidation.Text("name", request.Name, 120, errors);
        if (code.Length > 0 && !SkuPattern().IsMatch(code)) errors["code"] = ["Use letters, numbers, dots, hyphens or underscores; start with a letter or number."];
        BusinessValidation.ThrowIfInvalid(errors);
        var warehouse = new Warehouse { Code = code, Name = name };
        db.Warehouses.Add(warehouse);
        Audit("Warehouse.Created", "Warehouse", warehouse.Id, $"Warehouse {code}: {name} created.");
        await Save(ct, "A warehouse with this code already exists. Choose another code.");
        return new(warehouse.Id, warehouse.Code, warehouse.Name);
    }

    public async Task<OrderDetailView> CreateOrder(OrderWrite request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var notes = BusinessValidation.Text("notes", request.Notes, 2000, errors, false, true);
        if (!DateOnly.TryParseExact(request.ExpectedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expectedDate))
            errors["expectedDate"] = ["Choose a valid delivery date (YYYY-MM-DD)."];
        if (request.SupplierId == Guid.Empty) errors["supplierId"] = ["Choose a supplier."];
        if (request.WarehouseId == Guid.Empty) errors["warehouseId"] = ["Choose a destination warehouse."];
        if (request.Lines is null || request.Lines.Count is < 1 or > 50) errors["lines"] = ["An order needs 1 to 50 distinct product lines."];
        else
        {
            var seen = new HashSet<Guid>();
            for (var i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (line is null) { errors[$"lines.{i}"] = ["Enter a product, quantity and unit cost."]; continue; }
                if (line.ProductId == Guid.Empty || !seen.Add(line.ProductId)) errors[$"lines.{i}.productId"] = ["Choose a distinct product for every line."];
                BusinessValidation.PositiveQuantity($"lines.{i}.quantity", line.Quantity, errors);
                BusinessValidation.Money($"lines.{i}.unitCost", line.UnitCost, errors);
            }
        }
        BusinessValidation.ThrowIfInvalid(errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ids = request.Lines!.Select(x => x.ProductId).ToArray();
        await LockProducts(ids, ct);
        if (!await db.Suppliers.AnyAsync(x => x.Id == request.SupplierId, ct)) errors["supplierId"] = ["Choose an existing supplier."];
        if (!await db.Warehouses.AnyAsync(x => x.Id == request.WarehouseId, ct)) errors["warehouseId"] = ["Choose an existing warehouse."];
        var products = await db.Products.Where(x => ids.Contains(x.Id) && x.IsActive).ToDictionaryAsync(x => x.Id, ct);
        for (var i = 0; i < request.Lines!.Count; i++)
            if (!products.ContainsKey(request.Lines[i].ProductId)) errors[$"lines.{i}.productId"] = ["Choose an existing active product. Archived products cannot be added to new orders."];
        BusinessValidation.ThrowIfInvalid(errors);
        var order = new PurchaseOrder
        {
            Number = Reference("PO"), SupplierId = request.SupplierId, WarehouseId = request.WarehouseId,
            CreatedById = actor.Id, CreatedByName = actor.FullName, ExpectedDate = expectedDate, Notes = notes
        };
        order.Lines = request.Lines.Select(x => new PurchaseOrderLine
        {
            PurchaseOrderId = order.Id, ProductId = x.ProductId, Sku = products[x.ProductId].Sku,
            ProductName = products[x.ProductId].Name, Quantity = x.Quantity, UnitCost = x.UnitCost
        }).ToList();
        db.Orders.Add(order);
        Audit("Order.Created", "PurchaseOrder", order.Id, $"{order.Number} created with {order.Lines.Count} product lines.");
        await Save(ct);
        await transaction.CommitAsync(ct);
        return await new BusinessQueries(db).Order(order.Id, ct);
    }

    public async Task<OrderDetailView> Transition(Guid id, string operation, string? rawReason, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var reason = BusinessValidation.Text("reason", rawReason, 1000, errors, operation is "reject" or "cancel", true);
        BusinessValidation.ThrowIfInvalid(errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock($"order:{id:N}", ct);
        var order = await db.Orders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw BusinessQueries.Missing("Purchase order");
        string next;
        switch (operation)
        {
            case "submit":
                RequireState(order, OrderStatuses.Draft);
                next = OrderStatuses.PendingApproval;
                break;
            case "approve":
                RequireState(order, OrderStatuses.PendingApproval);
                if (order.CreatedById == actor.Id) throw new AppException(409, "Independent approval required", "You created this order. Ask another administrator or manager to approve it.");
                next = OrderStatuses.Approved;
                break;
            case "reject":
                RequireState(order, OrderStatuses.PendingApproval);
                next = OrderStatuses.Rejected;
                order.DecisionReason = reason;
                break;
            case "cancel":
                RequireState(order, OrderStatuses.Draft, OrderStatuses.PendingApproval, OrderStatuses.Approved);
                if (order.Lines.Any(x => x.ReceivedQuantity > 0)) throw new AppException(409, "Order cannot be cancelled", "This order has receipts. Record stock corrections separately with a reason.");
                next = OrderStatuses.Cancelled;
                order.DecisionReason = reason;
                break;
            default: throw new InvalidOperationException("Unknown purchasing transition.");
        }
        order.Status = next;
        Audit($"Order.{next}", "PurchaseOrder", id, $"{order.Number} moved to {next}{(reason.Length > 0 ? $": {reason}" : ".")}");
        await Save(ct);
        await transaction.CommitAsync(ct);
        return await new BusinessQueries(db).Order(id, ct);
    }

    public async Task<ReceiptResult> Receive(Guid id, string? rawKey, ReceiptWrite request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Guid.TryParse(rawKey, out var key) || key == Guid.Empty) errors["idempotencyKey"] = ["Send a non-empty UUID in the Idempotency-Key header. Retain it when retrying the same receipt."];
        var notes = BusinessValidation.Text("notes", request.Notes, 2000, errors, false, true);
        if (request.Lines is null || request.Lines.Count is < 1 or > 50) errors["lines"] = ["A receipt needs 1 to 50 distinct order lines."];
        else
        {
            var seen = new HashSet<Guid>();
            for (var i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (line is null) { errors[$"lines.{i}"] = ["Enter an order line and received quantity."]; continue; }
                if (line.OrderLineId == Guid.Empty || !seen.Add(line.OrderLineId)) errors[$"lines.{i}.orderLineId"] = ["Choose a distinct order line."];
                BusinessValidation.PositiveQuantity($"lines.{i}.quantity", line.Quantity, errors);
            }
        }
        BusinessValidation.ThrowIfInvalid(errors);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            orderId = id, notes, lines = request.Lines!.OrderBy(x => x.OrderLineId).Select(x => new { x.OrderLineId, x.Quantity })
        }))));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock($"order:{id:N}", ct);
        await Lock($"receipt-key:{key:N}", ct);
        var prior = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (prior is not null)
        {
            if (prior.Fingerprint != fingerprint || prior.CreatedById != actor.Id) throw new AppException(409, "Idempotency key already used", "This key belongs to a different receipt payload or owner. Use a new key for a new delivery.");
            await transaction.CommitAsync(ct);
            return new(prior.Id, await new BusinessQueries(db).Order(id, ct), true);
        }
        var order = await db.Orders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw BusinessQueries.Missing("Purchase order");
        RequireState(order, OrderStatuses.Approved, OrderStatuses.PartiallyReceived);
        var orderLines = order.Lines.ToDictionary(x => x.Id);
        for (var i = 0; i < request.Lines!.Count; i++)
        {
            var line = request.Lines[i];
            if (!orderLines.TryGetValue(line.OrderLineId, out var original)) errors[$"lines.{i}.orderLineId"] = ["This line does not belong to the selected order."];
            else if (line.Quantity > original.Quantity - original.ReceivedQuantity)
                throw new AppException(409, "Receipt exceeds remaining quantity", $"{original.Sku} has {original.Quantity - original.ReceivedQuantity} units remaining. Refresh the order before receiving.");
        }
        BusinessValidation.ThrowIfInvalid(errors);
        await LockProducts(request.Lines.Select(x => orderLines[x.OrderLineId].ProductId), ct);
        await Accounting.LockWarehouses(db, [order.WarehouseId], ct);
        await LockPositions(request.Lines.Select(x => (orderLines[x.OrderLineId].ProductId, order.WarehouseId)), ct);
        var standardCosts = await db.Products.Where(x => orderLines.Values.Select(l => l.ProductId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.StandardCost, ct);
        var receipt = new Receipt
        {
            PurchaseOrderId = order.Id, IdempotencyKey = key, Fingerprint = fingerprint, Number = Reference("RCV"),
            Notes = notes, CreatedById = actor.Id, CreatedByName = actor.FullName
        };
        foreach (var line in request.Lines)
        {
            var original = orderLines[line.OrderLineId];
            var balance = await Balance(original.ProductId, order.WarehouseId, ct);
            Increase(balance, line.Quantity);
            original.ReceivedQuantity += line.Quantity;
            receipt.Lines.Add(new ReceiptLine { ReceiptId = receipt.Id, OrderLineId = original.Id, Quantity = line.Quantity });
            Movement(original.ProductId, order.WarehouseId, line.Quantity, "Receipt", $"Goods received against {order.Number}.", receipt.Number, receipt.Id);
        }
        db.Receipts.Add(receipt);
        var inventoryValue = request.Lines.Sum(x => x.Quantity * standardCosts[orderLines[x.OrderLineId].ProductId]);
        var payable = request.Lines.Sum(x => x.Quantity * orderLines[x.OrderLineId].UnitCost);
        var purchaseVariance = payable - inventoryValue;
        Accounting.Post(db, "PurchaseReceipt", receipt.Id, await Accounting.BranchFor(db, order.WarehouseId, ct), $"Receipt {receipt.Number}",
            ("1200", inventoryValue, 0), ("2000", 0, payable),
            ("5100", Math.Max(purchaseVariance, 0), Math.Max(-purchaseVariance, 0)));
        order.Status = order.Lines.All(x => x.ReceivedQuantity == x.Quantity) ? OrderStatuses.Received : OrderStatuses.PartiallyReceived;
        Audit("Order.Received", "PurchaseOrder", order.Id, $"{receipt.Number} received against {order.Number}; order is {order.Status}.");
        await Save(ct);
        await transaction.CommitAsync(ct);
        return new(receipt.Id, await new BusinessQueries(db).Order(id, ct), false);
    }

    public async Task<Guid> Adjust(AdjustmentWrite request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var reason = BusinessValidation.Text("reason", request.Reason, 1000, errors, true, true);
        if (request.Quantity == 0 || (long)request.Quantity < -BusinessValidation.QuantityLimit || request.Quantity > BusinessValidation.QuantityLimit)
            errors["quantity"] = ["Enter a nonzero whole adjustment from -1000000000 to 1000000000."];
        BusinessValidation.ThrowIfInvalid(errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockProducts([request.ProductId], ct);
        await Accounting.LockWarehouses(db, [request.WarehouseId], ct);
        await EnsurePosition(request.ProductId, request.WarehouseId, ct);
        await LockPositions([(request.ProductId, request.WarehouseId)], ct);
        var balance = await Balance(request.ProductId, request.WarehouseId, ct);
        if (request.Quantity < 0 && balance.OnHand < -(long)request.Quantity) throw Insufficient(balance.OnHand);
        Increase(balance, request.Quantity);
        var referenceId = Guid.NewGuid();
        var movement = Movement(request.ProductId, request.WarehouseId, request.Quantity, "Adjustment", reason, Reference("ADJ"), referenceId);
        var adjustmentValue = Math.Abs((decimal)request.Quantity) * await db.Products.Where(x => x.Id == request.ProductId).Select(x => x.StandardCost).SingleAsync(ct);
        Accounting.Post(db, "Adjustment", movement.Id, await Accounting.BranchFor(db, request.WarehouseId, ct), reason,
            ("1200", request.Quantity > 0 ? adjustmentValue : 0, request.Quantity < 0 ? adjustmentValue : 0),
            (request.Quantity > 0 ? "3000" : "5100", request.Quantity < 0 ? adjustmentValue : 0, request.Quantity > 0 ? adjustmentValue : 0));
        Audit("Inventory.Adjusted", "StockMovement", movement.Id, $"Stock adjusted by {request.Quantity:+#;-#;0} units. {reason}");
        await Save(ct);
        await transaction.CommitAsync(ct);
        return movement.Id;
    }

    public async Task<Guid> Transfer(TransferWrite request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var reason = BusinessValidation.Text("reason", request.Reason, 1000, errors, true, true);
        BusinessValidation.PositiveQuantity("quantity", request.Quantity, errors);
        if (request.FromWarehouseId == request.ToWarehouseId) errors["toWarehouseId"] = ["Choose a different destination warehouse."];
        BusinessValidation.ThrowIfInvalid(errors);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockProducts([request.ProductId], ct);
        await Accounting.LockWarehouses(db, [request.FromWarehouseId, request.ToWarehouseId], ct);
        await EnsurePosition(request.ProductId, request.FromWarehouseId, ct, "fromWarehouseId");
        if (!await db.Warehouses.AnyAsync(x => x.Id == request.ToWarehouseId, ct))
            throw new AppException(400, "Invalid request", "Choose an existing destination warehouse.", new() { ["toWarehouseId"] = ["Choose an existing destination warehouse."] });
        await LockPositions([(request.ProductId, request.FromWarehouseId), (request.ProductId, request.ToWarehouseId)], ct);
        var source = await Balance(request.ProductId, request.FromWarehouseId, ct);
        if (source.OnHand < request.Quantity) throw Insufficient(source.OnHand);
        var destination = await Balance(request.ProductId, request.ToWarehouseId, ct);
        source.OnHand -= request.Quantity;
        Increase(destination, request.Quantity);
        var referenceId = Guid.NewGuid();
        var reference = Reference("TRF");
        Movement(request.ProductId, request.FromWarehouseId, -request.Quantity, "TransferOut", reason, reference, referenceId);
        Movement(request.ProductId, request.ToWarehouseId, request.Quantity, "TransferIn", reason, reference, referenceId);
        var transferValue = request.Quantity * await db.Products.Where(x => x.Id == request.ProductId).Select(x => x.StandardCost).SingleAsync(ct);
        Accounting.Post(db, "Transfer", referenceId, null, $"Stock transfer {reference}", ("1200", transferValue, 0), ("1200", 0, transferValue));
        Audit("Inventory.Transferred", "Transfer", referenceId, $"{reference}: {request.Quantity} units transferred. {reason}");
        await Save(ct);
        await transaction.CommitAsync(ct);
        return referenceId;
    }

    private async Task EnsurePosition(Guid productId, Guid warehouseId, CancellationToken ct, string warehouseField = "warehouseId")
    {
        var errors = new Dictionary<string, string[]>();
        if (!await db.Products.AnyAsync(x => x.Id == productId, ct)) errors["productId"] = ["Choose an existing product."];
        if (!await db.Warehouses.AnyAsync(x => x.Id == warehouseId, ct)) errors[warehouseField] = ["Choose an existing warehouse."];
        BusinessValidation.ThrowIfInvalid(errors);
    }

    private async Task<InventoryBalance> Balance(Guid productId, Guid warehouseId, CancellationToken ct)
    {
        var balance = await db.InventoryBalances.SingleOrDefaultAsync(x => x.ProductId == productId && x.WarehouseId == warehouseId, ct);
        if (balance is not null) return balance;
        balance = new InventoryBalance { ProductId = productId, WarehouseId = warehouseId };
        db.InventoryBalances.Add(balance);
        return balance;
    }

    private static void Increase(InventoryBalance balance, long quantity)
    {
        if (quantity > 0 && balance.OnHand > 9_007_199_254_740_991L - quantity)
            throw new AppException(409, "Stock limit reached", "This operation exceeds the supported whole-unit balance.");
        balance.OnHand += quantity;
    }

    private StockMovement Movement(Guid productId, Guid warehouseId, long quantity, string type, string reason, string reference, Guid referenceId)
    {
        var movement = new StockMovement { ProductId = productId, WarehouseId = warehouseId, Quantity = quantity,
            Type = type, Reason = reason, Reference = reference, ReferenceId = referenceId, ActorId = actor.Id, ActorName = actor.FullName };
        db.StockMovements.Add(movement);
        return movement;
    }

    private void Audit(string action, string entityType, Guid entityId, string description) => db.AuditEvents.Add(new AuditEvent
    {
        ActorId = actor.Id, ActorName = actor.FullName, Action = action, EntityType = entityType,
        EntityId = entityId.ToString(), Description = description
    });

    private async Task Save(CancellationToken ct, string conflict = "A record with the same identifier already exists. Refresh the page and try again.")
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new AppException(409, "Record already exists", conflict); }
    }

    private static void RequireState(PurchaseOrder order, params string[] allowed)
    {
        if (!allowed.Contains(order.Status)) throw new AppException(409, "Order state changed", $"{order.Number} is {order.Status}. This action requires {string.Join(" or ", allowed)}. Refresh the order to continue.");
    }

    private Task Lock(string key, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

    private async Task LockProducts(IEnumerable<Guid> productIds, CancellationToken ct)
    {
        // Catalog edits and new orders share these locks so an archive cannot race the active-product check.
        foreach (var key in productIds.Select(id => $"product:{id:N}").Distinct().Order(StringComparer.Ordinal))
            await Lock(key, ct);
    }

    private async Task LockPositions(IEnumerable<(Guid ProductId, Guid WarehouseId)> positions, CancellationToken ct)
    {
        // All receipts, adjustments and transfers acquire the same position locks in this order.
        foreach (var key in positions.Select(x => $"stock:{x.ProductId:N}:{x.WarehouseId:N}").Distinct().Order(StringComparer.Ordinal))
            await Lock(key, ct);
    }

    private static AppException Insufficient(long available) => new(409, "Insufficient stock", $"Only {available} units are available in the source warehouse. Refresh inventory and reduce the quantity.");
    private static string Reference(string prefix) => $"{prefix}-{DateTime.UtcNow:yyMMdd}-{Guid.NewGuid():N}"[..(prefix.Length + 1 + 6 + 1 + 12)].ToUpperInvariant();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
}
