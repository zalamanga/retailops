using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Domain;

namespace RetailOps.Api.Data;

public static class BusinessModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<Product>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(180);
            e.Property(x => x.Unit).HasMaxLength(24);
            e.Property(x => x.StandardCost).HasPrecision(18, 2);
            e.HasIndex(x => x.Sku).IsUnique();
            e.HasIndex(x => x.Name);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Products_ReorderPoint", "\"ReorderPoint\" >= 0");
                t.HasCheckConstraint("CK_Products_StandardCost", "\"StandardCost\" >= 0");
            });
        });
        model.Entity<Supplier>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(180);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.ToTable(t => t.HasCheckConstraint("CK_Suppliers_LeadTimeDays", "\"LeadTimeDays\" >= 0"));
        });
        model.Entity<Warehouse>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => x.Code).IsUnique();
        });
        model.Entity<PurchaseOrder>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.DecisionReason).HasMaxLength(1000);
            e.Property(x => x.CreatedByName).HasMaxLength(160);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Orders_Status", "\"Status\" IN ('Draft','PendingApproval','Approved','PartiallyReceived','Received','Rejected','Cancelled')"));
        });
        model.Entity<PurchaseOrderLine>(e =>
        {
            e.Property(x => x.Sku).HasMaxLength(64);
            e.Property(x => x.ProductName).HasMaxLength(180);
            e.Property(x => x.UnitCost).HasPrecision(18, 2);
            e.HasOne(x => x.PurchaseOrder).WithMany(x => x.Lines).HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PurchaseOrderId, x.ProductId }).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_OrderLines_Quantity", "\"Quantity\" > 0 AND \"ReceivedQuantity\" >= 0 AND \"ReceivedQuantity\" <= \"Quantity\"");
                t.HasCheckConstraint("CK_OrderLines_UnitCost", "\"UnitCost\" >= 0");
            });
        });
        model.Entity<Receipt>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(40);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.CreatedByName).HasMaxLength(160);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne(x => x.PurchaseOrder).WithMany(x => x.Receipts).HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ReceiptLine>(e =>
        {
            e.HasOne(x => x.Receipt).WithMany(x => x.Lines).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OrderLine).WithMany().HasForeignKey(x => x.OrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ReceiptId, x.OrderLineId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_ReceiptLines_Quantity", "\"Quantity\" > 0"));
        });
        model.Entity<InventoryBalance>(e =>
        {
            e.HasKey(x => new { x.ProductId, x.WarehouseId });
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_InventoryBalances_OnHand", "\"OnHand\" >= 0"));
        });
        model.Entity<StockMovement>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(24);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Reference).HasMaxLength(80);
            e.Property(x => x.ActorName).HasMaxLength(160);
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.HasIndex(x => new { x.ProductId, x.WarehouseId });
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_StockMovements_Quantity", "\"Quantity\" <> 0");
                t.HasCheckConstraint("CK_StockMovements_Type", "\"Type\" IN ('Receipt','Adjustment','TransferIn','TransferOut','Sale','Return')");
            });
        });
        model.Entity<AuditEvent>(e =>
        {
            e.Property(x => x.ActorName).HasMaxLength(160);
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.EntityType).HasMaxLength(80);
            e.Property(x => x.EntityId).HasMaxLength(80);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasIndex(x => new { x.CreatedAt, x.Id });
        });
    }
}
