using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Data;
using RetailOps.Api.Security;
using RetailOps.Api.Services;

namespace RetailOps.Api.Endpoints;

public static class RetailEndpoints
{
    public static void MapRetailEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/retail").RequireAuthorization("Retail").WithTags("Retail")
            .AddEndpointFilter(VerifyQueuedIdentity);
        api.MapGet("/branches", (AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Branches(ct));
        api.MapPost("/branches", (BranchWrite request, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).CreateBranch(request, ct)).RequireAuthorization("Admin");
        api.MapGet("/assignments", (AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Assignments(ct)).RequireAuthorization("Admin");
        api.MapPost("/assignments", async (AssignmentWrite request, AppDbContext db, HttpContext ctx, CancellationToken ct) =>
        { await new RetailService(db, ctx.Actor()).Assign(request.UserId, request.BranchId, false, ct); return Results.Ok(new { request.UserId, request.BranchId }); }).RequireAuthorization("Admin");
        api.MapDelete("/assignments/{userId:guid}/{branchId:guid}", async (Guid userId, Guid branchId, AppDbContext db, HttpContext ctx, CancellationToken ct) =>
        { await new RetailService(db, ctx.Actor()).Assign(userId, branchId, true, ct); return Results.NoContent(); }).RequireAuthorization("Admin");
        api.MapGet("/prices", (AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Prices(ct)).RequireAuthorization("Catalog");
        api.MapPut("/prices/{id:guid}", (Guid id, PriceWrite request, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).SetPrice(id, request, ct)).RequireAuthorization("Admin");
        api.MapGet("/catalog", (Guid branchId, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Catalog(branchId, ct));
        api.MapGet("/shifts", (Guid? branchId, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Shifts(branchId, ct));
        api.MapPost("/shifts", (ShiftWrite request, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).OpenShift(ctx.Request.Headers["Idempotency-Key"], request, ct));
        api.MapPost("/shifts/{id:guid}/close", (Guid id, CloseShiftWrite request, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).CloseShift(id, ctx.Request.Headers["Idempotency-Key"], request, ct));
        api.MapPost("/sales", (SaleWrite request, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Sell(ctx.Request.Headers["Idempotency-Key"], request, ct));
        api.MapGet("/sales", (Guid? branchId, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Sales(branchId, ct));
        api.MapGet("/sales/{id:guid}", (Guid id, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Sale(id, ct));
        api.MapPost("/sales/{id:guid}/returns", (Guid id, ReturnWrite request, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Return(id, ctx.Request.Headers["Idempotency-Key"], request, ct)).RequireAuthorization("Catalog");
        api.MapGet("/journals", (AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Journals(ct)).RequireAuthorization("Catalog");
        api.MapGet("/reports", (string? from, string? to, Guid? branchId, AppDbContext db, HttpContext ctx, CancellationToken ct) => new RetailService(db, ctx.Actor()).Reports(from, to, branchId, ct)).RequireAuthorization("Catalog");
    }

    private static async ValueTask<object?> VerifyQueuedIdentity(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
            return await next(invocation);
        var hasActor = request.Headers.ContainsKey("X-Retail-Actor");
        var hasOrganization = request.Headers.ContainsKey("X-Retail-Organization");
        if (!hasActor && !hasOrganization) return await next(invocation);
        var actors = request.Headers["X-Retail-Actor"];
        var organizations = request.Headers["X-Retail-Organization"];
        if (!hasActor || !hasOrganization || actors.Count != 1 || organizations.Count != 1
            || !Guid.TryParse(actors[0], out var expectedActor) || expectedActor == Guid.Empty
            || !Guid.TryParse(organizations[0], out var expectedOrganization) || expectedOrganization == Guid.Empty)
            throw QueuedIdentityChanged();
        var actor = context.Actor();
        if (actor.Id != expectedActor) throw QueuedIdentityChanged();
        var db = context.RequestServices.GetRequiredService<AppDbContext>();
        var ct = context.RequestAborted;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == expectedActor && x.IsActive, ct)) throw QueuedIdentityChanged();
        var workspace = await db.Organizations.AsNoTracking().Select(x => x.Id).Take(2).ToListAsync(ct);
        if (workspace.Count != 1 || workspace[0] != expectedOrganization) throw QueuedIdentityChanged();
        return await next(invocation);
    }

    private static AppException QueuedIdentityChanged() => new(403, "Queued identity changed",
        "The active account or organization differs from the owner of this queued command. Sign in as its original owner before retrying.");
}
