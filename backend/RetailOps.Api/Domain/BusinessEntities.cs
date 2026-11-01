namespace RetailOps.Api.Domain;

public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public int ReorderPoint { get; set; }
    public decimal StandardCost { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public int LeadTimeDays { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Warehouse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class OrderStatuses
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string PartiallyReceived = "PartiallyReceived";
    public const string Received = "Received";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Draft, PendingApproval, Approved, PartiallyReceived, Received, Rejected, Cancelled];
}

public sealed class PurchaseOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public Guid WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public Guid CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public string Status { get; set; } = OrderStatuses.Draft;
    public DateOnly ExpectedDate { get; set; }
    public string Notes { get; set; } = "";
    public string DecisionReason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PurchaseOrderLine> Lines { get; set; } = [];
    public List<Receipt> Receipts { get; set; } = [];
}

public sealed class PurchaseOrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string Sku { get; set; } = "";
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public int ReceivedQuantity { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class Receipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
    public Guid IdempotencyKey { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Number { get; set; } = "";
    public Guid CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReceiptLine> Lines { get; set; } = [];
}

public sealed class ReceiptLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReceiptId { get; set; }
    public Receipt Receipt { get; set; } = null!;
    public Guid OrderLineId { get; set; }
    public PurchaseOrderLine OrderLine { get; set; } = null!;
    public int Quantity { get; set; }
}

public sealed class InventoryBalance
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public long OnHand { get; set; }
}

public sealed class StockMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
    public long Quantity { get; set; }
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Reference { get; set; } = "";
    public Guid ReferenceId { get; set; }
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
