using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Auth;
using RetailOps.Api.Domain;

namespace RetailOps.Api.Data;

public static class RetailModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<Branch>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique(); e.HasIndex(x => x.WarehouseId).IsUnique();
            e.Property(x => x.Code).HasMaxLength(32); e.Property(x => x.Name).HasMaxLength(120);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BranchAssignment>(e =>
        {
            e.HasKey(x => new { x.UserId, x.BranchId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ProductPrice>(e =>
        {
            e.HasKey(x => x.ProductId);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RetailPrice).HasPrecision(18, 2); e.Property(x => x.WholesalePrice).HasPrecision(18, 2);
            e.ToTable(t => t.HasCheckConstraint("CK_ProductPrices_Values", "\"RetailPrice\">0 AND \"WholesalePrice\">0 AND \"WholesalePrice\"<=\"RetailPrice\" AND \"WholesaleMinimum\">=2 AND \"Version\">0"));
        });
        model.Entity<CashShift>(e =>
        {
            e.Property(x => x.OpenedByName).HasMaxLength(100); e.Property(x => x.Status).HasMaxLength(10);
            foreach (var name in new[] { "OpeningCash", "CountedCash", "ExpectedCash", "Variance" }) e.Property(name).HasPrecision(28, 2);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OpenedById).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OpenedById).IsUnique().HasFilter("\"Status\"='Open'");
            e.ToTable(t => t.HasCheckConstraint("CK_CashShifts_State", "\"OpeningCash\">=0 AND ((\"Status\"='Open' AND \"ClosedAt\" IS NULL AND \"CountedCash\" IS NULL AND \"ExpectedCash\" IS NULL AND \"Variance\" IS NULL) OR (\"Status\"='Closed' AND \"ClosedAt\" IS NOT NULL AND \"CountedCash\">=0 AND \"ExpectedCash\" IS NOT NULL AND \"Variance\"=\"CountedCash\"-\"ExpectedCash\"))"));
        });
        model.Entity<RetailSale>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique(); e.Property(x => x.Number).HasMaxLength(40); e.Property(x => x.ActorName).HasMaxLength(100);
            e.Property(x => x.PriceTier).HasMaxLength(16);
            foreach (var name in new[] { "Total", "CashTendered", "Change" }) e.Property(name).HasPrecision(18, 2);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BranchId, x.CreatedAt });
            e.ToTable(t => t.HasCheckConstraint("CK_RetailSales_Values", "\"Total\">0 AND \"CashTendered\">=\"Total\" AND \"Change\"=\"CashTendered\"-\"Total\" AND \"PriceTier\" IN ('Retail','Wholesale')"));
        });
        model.Entity<RetailSaleLine>(e =>
        {
            e.Property(x => x.Sku).HasMaxLength(64); e.Property(x => x.Name).HasMaxLength(180);
            foreach (var name in new[] { "UnitPrice", "StandardCost", "LineTotal" }) e.Property(name).HasPrecision(28, 2);
            e.HasOne(x => x.Sale).WithMany(x => x.Lines).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SaleId, x.ProductId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_RetailSaleLines_Values", "\"Quantity\">0 AND \"UnitPrice\">0 AND \"StandardCost\">=0 AND \"LineTotal\"=\"Quantity\"*\"UnitPrice\""));
        });
        model.Entity<RetailReturn>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique(); e.Property(x => x.Number).HasMaxLength(40); e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.HasOne(x => x.Sale).WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_RetailReturns_Total", "\"Total\">0"));
        });
        model.Entity<RetailReturnLine>(e =>
        {
            e.HasOne(x => x.Return).WithMany(x => x.Lines).HasForeignKey(x => x.ReturnId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SaleLine).WithMany().HasForeignKey(x => x.SaleLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ReturnId, x.SaleLineId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_RetailReturnLines_Quantity", "\"Quantity\">0"));
        });
        model.Entity<RetailMutation>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(40); e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.ResponseJson).HasColumnType("text");
        });
        model.Entity<FinancialAccount>(e =>
        {
            e.HasKey(x => x.Code); e.Property(x => x.Code).HasMaxLength(4); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Type).HasMaxLength(16);
            e.HasData(new FinancialAccount { Code="1000",Name="Cash",Type="Asset" }, new FinancialAccount { Code="1200",Name="Inventory",Type="Asset" },
                new FinancialAccount { Code="2000",Name="Trade payables",Type="Liability" },new FinancialAccount { Code="3000",Name="Opening equity",Type="Equity" },
                new FinancialAccount { Code="4000",Name="Sales revenue",Type="Revenue" },new FinancialAccount { Code="5000",Name="Cost of goods sold",Type="Expense" },
                new FinancialAccount { Code="5100",Name="Inventory and purchase variance",Type="Expense" });
        });
        model.Entity<JournalEntry>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(40); e.Property(x => x.SourceType).HasMaxLength(40); e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.Number).IsUnique(); e.HasIndex(x => new { x.SourceType, x.SourceId }).IsUnique();
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CreatedAt);
        });
        model.Entity<JournalLine>(e =>
        {
            e.Property(x => x.Debit).HasPrecision(38, 2); e.Property(x => x.Credit).HasPrecision(38, 2);
            e.HasOne(x => x.JournalEntry).WithMany(x => x.Lines).HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountCode).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_JournalLines_OneSide", "(\"Debit\">0 AND \"Credit\"=0) OR (\"Credit\">0 AND \"Debit\"=0)"));
        });
    }
}
