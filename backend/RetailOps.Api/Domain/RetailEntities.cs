namespace RetailOps.Api.Domain;

public sealed class Branch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid WarehouseId { get; set; }
    public Warehouse Warehouse { get; set; } = null!;
}
public sealed class BranchAssignment
{
    public Guid UserId { get; set; }
    public Guid BranchId { get; set; }
}
public sealed class ProductPrice
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public decimal RetailPrice { get; set; }
    public decimal WholesalePrice { get; set; }
    public int WholesaleMinimum { get; set; }
    public long Version { get; set; }
}
public sealed class CashShift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Branch Branch { get; set; } = null!;
    public Guid OpenedById { get; set; }
    public string OpenedByName { get; set; } = "";
    public decimal OpeningCash { get; set; }
    public string Status { get; set; } = "Open";
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? Variance { get; set; }
}
public sealed class RetailSale
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid BranchId { get; set; }
    public Branch Branch { get; set; } = null!;
    public Guid ShiftId { get; set; }
    public CashShift Shift { get; set; } = null!;
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string PriceTier { get; set; } = "";
    public decimal Total { get; set; }
    public decimal CashTendered { get; set; }
    public decimal Change { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ClientCreatedAt { get; set; }
    public List<RetailSaleLine> Lines { get; set; } = [];
}
public sealed class RetailSaleLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SaleId { get; set; }
    public RetailSale Sale { get; set; } = null!;
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal StandardCost { get; set; }
    public decimal LineTotal { get; set; }
}
public sealed class RetailReturn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid SaleId { get; set; }
    public RetailSale Sale { get; set; } = null!;
    public Guid ShiftId { get; set; }
    public CashShift Shift { get; set; } = null!;
    public Guid BranchId { get; set; }
    public Guid ActorId { get; set; }
    public decimal Total { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RetailReturnLine> Lines { get; set; } = [];
}
public sealed class RetailReturnLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReturnId { get; set; }
    public RetailReturn Return { get; set; } = null!;
    public Guid SaleLineId { get; set; }
    public RetailSaleLine SaleLine { get; set; } = null!;
    public int Quantity { get; set; }
}
public sealed class RetailMutation
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Kind { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ResponseJson { get; set; } = "";
}
public sealed class FinancialAccount
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
public sealed class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public string SourceType { get; set; } = "";
    public Guid SourceId { get; set; }
    public Guid? BranchId { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<JournalLine> Lines { get; set; } = [];
}
public sealed class JournalLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = null!;
    public string AccountCode { get; set; } = "";
    public FinancialAccount Account { get; set; } = null!;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}
