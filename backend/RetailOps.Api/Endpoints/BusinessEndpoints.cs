using RetailOps.Api.Data;
using RetailOps.Api.Security;
using RetailOps.Api.Services;

namespace RetailOps.Api.Endpoints;

public static class BusinessEndpoints
{
    public static void MapBusinessEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("Backoffice");

        api.MapGet("/products", (AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessQueries(db).Products(context.Request, ct)).WithTags("Catalog");
        api.MapPost("/products", (ProductWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).SaveProduct(null, request, ct)).RequireAuthorization("Catalog").WithTags("Catalog");
        api.MapPut("/products/{id:guid}", (Guid id, ProductWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).SaveProduct(id, request, ct)).RequireAuthorization("Catalog").WithTags("Catalog");

        api.MapGet("/suppliers", (AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessQueries(db).Suppliers(context.Request, ct)).WithTags("Suppliers");
        api.MapPost("/suppliers", (SupplierWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).SaveSupplier(null, request, ct)).RequireAuthorization("Catalog").WithTags("Suppliers");
        api.MapPut("/suppliers/{id:guid}", (Guid id, SupplierWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).SaveSupplier(id, request, ct)).RequireAuthorization("Catalog").WithTags("Suppliers");

        api.MapGet("/warehouses", (AppDbContext db, CancellationToken ct) => new BusinessQueries(db).Warehouses(ct)).WithTags("Warehouses");
        api.MapPost("/warehouses", (WarehouseWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).CreateWarehouse(request, ct)).RequireAuthorization("Catalog").WithTags("Warehouses");

        api.MapGet("/orders", (AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessQueries(db).Orders(context.Request, ct)).WithTags("Purchasing");
        api.MapGet("/orders/{id:guid}", (Guid id, AppDbContext db, CancellationToken ct) => new BusinessQueries(db).Order(id, ct)).WithTags("Purchasing");
        api.MapPost("/orders", (OrderWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).CreateOrder(request, ct)).RequireAuthorization("Operate").WithTags("Purchasing");
        api.MapPost("/orders/{id:guid}/submit", (Guid id, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).Transition(id, "submit", null, ct)).RequireAuthorization("Operate").WithTags("Purchasing");
        api.MapPost("/orders/{id:guid}/approve", (Guid id, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).Transition(id, "approve", null, ct)).RequireAuthorization("Approve").WithTags("Purchasing");
        api.MapPost("/orders/{id:guid}/reject", (Guid id, ReasonWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).Transition(id, "reject", request.Reason, ct)).RequireAuthorization("Approve").WithTags("Purchasing");
        api.MapPost("/orders/{id:guid}/cancel", (Guid id, ReasonWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).Transition(id, "cancel", request.Reason, ct)).RequireAuthorization("Operate").WithTags("Purchasing");
        api.MapPost("/orders/{id:guid}/receipts", (Guid id, ReceiptWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessMutations(db, context.Actor()).Receive(id, context.Request.Headers["Idempotency-Key"].ToString(), request, ct))
            .RequireAuthorization("Operate").WithTags("Receiving");

        api.MapGet("/inventory", (AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessQueries(db).Inventory(context.Request, ct)).WithTags("Inventory");
        api.MapGet("/inventory/export", async (AppDbContext db, HttpContext context, CancellationToken ct) =>
            Results.File(await new BusinessQueries(db).InventoryCsv(context.Request, ct), "text/csv; charset=utf-8", $"inventory-{DateTime.UtcNow:yyyy-MM-dd}.csv"))
            .WithTags("Inventory");
        api.MapPost("/inventory/adjustments", async (AdjustmentWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            Results.Ok(new { movementId = await new BusinessMutations(db, context.Actor()).Adjust(request, ct) })).RequireAuthorization("Operate").WithTags("Inventory");
        api.MapPost("/inventory/transfers", async (TransferWrite request, AppDbContext db, HttpContext context, CancellationToken ct) =>
            Results.Ok(new { referenceId = await new BusinessMutations(db, context.Actor()).Transfer(request, ct) })).RequireAuthorization("Operate").WithTags("Inventory");
        api.MapGet("/movements", (AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessQueries(db).Movements(context.Request, ct)).WithTags("Inventory");
        api.MapGet("/dashboard", (AppDbContext db, CancellationToken ct) => new BusinessQueries(db).Dashboard(ct)).WithTags("Overview");
        api.MapGet("/activity", (AppDbContext db, HttpContext context, CancellationToken ct) =>
            new BusinessQueries(db).Activity(context.Request, ct)).WithTags("Activity");
    }
}
