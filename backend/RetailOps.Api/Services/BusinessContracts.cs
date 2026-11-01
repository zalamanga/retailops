namespace RetailOps.Api.Services;

public sealed record ProductWrite(string? Sku, string? Name, string? Unit, int ReorderPoint, decimal StandardCost, bool IsActive = true);
public sealed record SupplierWrite(string? Name, string? Email, string? Phone, int LeadTimeDays);
public sealed record WarehouseWrite(string? Code, string? Name);
public sealed record OrderLineWrite(Guid ProductId, int Quantity, decimal UnitCost);
public sealed record OrderWrite(Guid SupplierId, Guid WarehouseId, string? ExpectedDate, string? Notes, List<OrderLineWrite>? Lines);
public sealed record ReasonWrite(string? Reason);
public sealed record ReceiptLineWrite(Guid OrderLineId, int Quantity);
public sealed record ReceiptWrite(string? Notes, List<ReceiptLineWrite>? Lines);
public sealed record AdjustmentWrite(Guid ProductId, Guid WarehouseId, int Quantity, string? Reason);
public sealed record TransferWrite(Guid ProductId, Guid FromWarehouseId, Guid ToWarehouseId, int Quantity, string? Reason);

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record ProductView(Guid Id, string Sku, string Name, string Unit, int ReorderPoint, decimal StandardCost, bool IsActive);
public sealed record SupplierView(Guid Id, string Name, string Email, string Phone, int LeadTimeDays);
public sealed record WarehouseView(Guid Id, string Code, string Name);
public sealed record OrderSummaryView(Guid Id, string Number, string SupplierName, string WarehouseName, string Status,
    decimal Total, DateOnly ExpectedDate, DateTimeOffset CreatedAt, string CreatedByName);
public sealed record OrderLineView(Guid Id, Guid ProductId, string Sku, string ProductName, int Quantity, int ReceivedQuantity,
    decimal UnitCost, decimal LineTotal);
public sealed record ReceiptLineView(Guid OrderLineId, int Quantity);
public sealed record ReceiptView(Guid Id, string Number, DateTimeOffset CreatedAt, string CreatedByName, string Notes,
    IReadOnlyList<ReceiptLineView> Lines);
public sealed record OrderDetailView(Guid Id, string Number, string SupplierName, string WarehouseName, string Status,
    decimal Total, DateOnly ExpectedDate, DateTimeOffset CreatedAt, string CreatedByName, Guid SupplierId, Guid WarehouseId,
    Guid CreatedById, string Notes, string DecisionReason, IReadOnlyList<OrderLineView> Lines, IReadOnlyList<ReceiptView> Receipts);
public sealed record ReceiptResult(Guid ReceiptId, OrderDetailView Order, bool Replayed);
public sealed record InventoryView
{
    public Guid ProductId { get; init; }
    public Guid WarehouseId { get; init; }
    public required string Sku { get; init; }
    public required string ProductName { get; init; }
    public required string Unit { get; init; }
    public required string WarehouseName { get; init; }
    public long OnHand { get; init; }
    public int ReorderPoint { get; init; }
    public decimal StandardCost { get; init; }
    public decimal StockValue { get; init; }
    public bool IsLowStock { get; init; }
}
public sealed record MovementView(Guid Id, string Sku, string ProductName, string WarehouseName, long Quantity, string Type,
    string Reason, string Reference, DateTimeOffset CreatedAt, string ActorName);
public sealed record ActivityView(Guid Id, string ActorName, string Action, string EntityType, string EntityId, string Description,
    DateTimeOffset CreatedAt);
public sealed record MovementDayView(DateOnly Date, long Incoming, long Outgoing);
public sealed record DashboardView(int ActiveProducts, decimal StockValue, int LowStockCount, int OpenOrders,
    IReadOnlyList<MovementDayView> MovementSeries, IReadOnlyList<InventoryView> LowStock, IReadOnlyList<OrderSummaryView> RecentOrders);
