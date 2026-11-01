export interface Branch { id: string; code: string; name: string; warehouseId: string; warehouseName: string }
export interface Assignment { userId: string; branchId: string }
export interface Price { productId: string; sku: string; name: string; retailPrice: number; wholesalePrice: number; wholesaleMinimum: number; version: number }
export interface CatalogProduct extends Price { unit: string; onHand: number }
export interface RetailCatalog { branch: Branch; products: CatalogProduct[]; capturedAt: string }
export interface Shift {
  id: string; branchId: string; branchName: string; openedById: string; openedByName: string;
  openingCash: number; status: string; openedAt: string; closedAt: string | null;
  countedCash: number | null; expectedCash: number; variance: number | null;
}
export interface SaleCommand {
  branchId: string; shiftId: string; priceTier: 'Retail' | 'Wholesale'; clientCreatedAt: string;
  lines: { productId: string; quantity: number; unitPrice: number; priceVersion: number }[];
  cashTendered: number;
}
export interface SaleSummary {
  id: string; number: string; branchId: string; branchName: string; shiftId: string;
  actorId: string; actorName: string; priceTier: string; total: number; createdAt: string;
}
export interface Sale extends SaleSummary {
  cashTendered: number; change: number; clientCreatedAt: string; replayed: boolean;
  lines: { id: string; productId: string; sku: string; name: string; quantity: number; unitPrice: number; lineTotal: number; returnedQuantity: number }[];
}
export interface SaleReturn { id: string; number: string; saleId: string; shiftId: string; total: number; createdAt: string; replayed: boolean }
export interface Journal {
  id: string; number: string; sourceType: string; sourceId: string; branchId: string | null; description: string;
  createdAt: string; totalDebit: number; totalCredit: number;
  lines: { accountCode: string; accountName: string; debit: number; credit: number }[];
}
export interface RetailReport {
  from: string; to: string; branchId: string | null; salesCount: number; grossSales: number;
  refunds: number; netSales: number; costOfGoodsSold: number; varianceExpense: number; netProfit: number;
  trialBalance: { code: string; name: string; type: string; debit: number; credit: number; balance: number }[];
  balanceSheet: { asOf: string; assets: number; liabilities: number; equity: number; retainedEarnings: number; balanced: boolean };
  journalBalanced: boolean;
}
