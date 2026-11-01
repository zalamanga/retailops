namespace RetailOps.Api.Services;

public sealed record BranchWrite(string? Code, string? Name, Guid WarehouseId);
public sealed record AssignmentWrite(Guid UserId, Guid BranchId);
public sealed record PriceWrite(decimal RetailPrice, decimal WholesalePrice, int WholesaleMinimum, long ExpectedVersion);
public sealed record ShiftWrite(Guid BranchId, decimal OpeningCash);
public sealed record CloseShiftWrite(decimal CountedCash);
public sealed record SaleLineWrite(Guid ProductId, int Quantity, decimal UnitPrice, long PriceVersion);
public sealed record SaleWrite(Guid BranchId, Guid ShiftId, string? PriceTier, DateTimeOffset ClientCreatedAt, List<SaleLineWrite>? Lines, decimal CashTendered);
public sealed record ReturnLineWrite(Guid SaleLineId, int Quantity);
public sealed record ReturnWrite(Guid ShiftId, string? Reason, List<ReturnLineWrite>? Lines);
