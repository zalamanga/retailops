using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Auth;
using RetailOps.Api.Domain;

namespace RetailOps.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> OrderLines => Set<PurchaseOrderLine>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ReceiptLine> ReceiptLines => Set<ReceiptLine>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<BranchAssignment> BranchAssignments => Set<BranchAssignment>();
    public DbSet<ProductPrice> ProductPrices => Set<ProductPrice>();
    public DbSet<CashShift> CashShifts => Set<CashShift>();
    public DbSet<RetailSale> RetailSales => Set<RetailSale>();
    public DbSet<RetailSaleLine> RetailSaleLines => Set<RetailSaleLine>();
    public DbSet<RetailReturn> RetailReturns => Set<RetailReturn>();
    public DbSet<RetailReturnLine> RetailReturnLines => Set<RetailReturnLine>();
    public DbSet<RetailMutation> RetailMutations => Set<RetailMutation>();
    public DbSet<FinancialAccount> FinancialAccounts => Set<FinancialAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuthModel.Configure(modelBuilder);
        BusinessModel.Configure(modelBuilder);
        RetailModel.Configure(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnlyRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAppendOnlyRecords();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardAppendOnlyRecords()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditEvent or StockMovement or Receipt or ReceiptLine or JournalEntry or JournalLine or FinancialAccount or RetailSale or RetailSaleLine or RetailReturn or RetailReturnLine or RetailMutation &&
                entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Activity, receipt and stock ledger records are append-only.");
            if (entry.Entity is JournalLine line && entry.State == EntityState.Added &&
                !ChangeTracker.Entries<JournalEntry>().Any(header => header.State == EntityState.Added && header.Entity.Id == line.JournalEntryId))
                throw new InvalidOperationException("Journal lines can only be added with a new journal header.");
        }
    }
}
