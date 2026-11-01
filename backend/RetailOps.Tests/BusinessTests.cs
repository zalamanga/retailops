using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailOps.Api.Data;
using Xunit;

namespace RetailOps.Tests;

public sealed class BusinessTests : ApiTest
{
    [Fact]
    public async Task Purchasing_requires_independent_approval_and_persists_partial_and_final_receipts()
    {
        await Setup();
        var catalog = await Catalog();
        using var manager = await CreateAccount("Manager");
        var order = await Order(catalog, 12);
        var id = order.GetProperty("id").GetGuid();
        var line = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        Assert.Equal("Draft", order.GetProperty("status").GetString());
        Assert.Equal(30000m, order.GetProperty("total").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await Receive(id, line, 1)).StatusCode);
        await Ok(await Send(Client, $"/api/orders/{id}/submit"));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, $"/api/orders/{id}/approve")).StatusCode);
        await Ok(await Send(manager, $"/api/orders/{id}/approve"));
        var partial = await Ok(await Receive(id, line, 5));
        Assert.Equal("PartiallyReceived", partial.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(5, partial.GetProperty("order").GetProperty("lines")[0].GetProperty("receivedQuantity").GetInt32());
        var received = await Ok(await Receive(id, line, 7));
        Assert.Equal("Received", received.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(2, received.GetProperty("order").GetProperty("receipts").GetArrayLength());
        Assert.Equal(12, await OnHand(catalog.Product, catalog.Warehouse));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, $"/api/orders/{id}/cancel", new { reason = "Already received" })).StatusCode);
        var movements = await Get($"/api/movements?productId={catalog.Product}&warehouseId={catalog.Warehouse}");
        Assert.Equal(2, movements.GetProperty("total").GetInt32());
        Assert.Equal(12, movements.GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("quantity").GetInt64()));
        var dashboard = await Get("/api/dashboard");
        Assert.Equal(30000m, dashboard.GetProperty("stockValue").GetDecimal());
        Assert.Equal(0, dashboard.GetProperty("openOrders").GetInt32());
        Assert.Equal(7, dashboard.GetProperty("movementSeries").GetArrayLength());
        Assert.Equal(12, dashboard.GetProperty("movementSeries").EnumerateArray().Sum(x => x.GetProperty("incoming").GetInt64()));
    }

    [Fact]
    public async Task Idempotent_receipt_replay_returns_same_receipt_and_conflicting_payload_cannot_change_stock()
    {
        await Setup();
        var catalog = await Catalog();
        var (id, line) = await ApprovedOrder(catalog);
        var key = Guid.NewGuid().ToString();
        var original = await Ok(await Receive(id, line, 4, key));
        var replay = await Ok(await Receive(id, line, 4, key));
        Assert.Equal(original.GetProperty("receiptId").GetGuid(), replay.GetProperty("receiptId").GetGuid());
        Assert.False(original.GetProperty("replayed").GetBoolean());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Receive(id, line, 5, key)).StatusCode);
        Assert.Equal(4, await OnHand(catalog.Product, catalog.Warehouse));
        Assert.Equal(1, (await Get($"/api/movements?productId={catalog.Product}")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Get($"/api/orders/{id}")).GetProperty("receipts").GetArrayLength());
        var another = await Order(catalog);
        Assert.Equal(HttpStatusCode.Conflict, (await Receive(another.GetProperty("id").GetGuid(),
            another.GetProperty("lines")[0].GetProperty("id").GetGuid(), 4, key)).StatusCode);
    }

    [Fact]
    public async Task Overreceipt_and_invalid_order_lines_leave_order_balance_and_ledger_unchanged()
    {
        await Setup();
        var catalog = await Catalog();
        var (id, line) = await ApprovedOrder(catalog);
        await Ok(await Receive(id, line, 4));
        Assert.Equal(HttpStatusCode.Conflict, (await Receive(id, line, 7)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Receive(id, Guid.NewGuid(), 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Receive(id, line, -1)).StatusCode);
        Assert.Equal(4, await OnHand(catalog.Product, catalog.Warehouse));
        var order = await Get($"/api/orders/{id}");
        Assert.Equal("PartiallyReceived", order.GetProperty("status").GetString());
        Assert.Equal(4, order.GetProperty("lines")[0].GetProperty("receivedQuantity").GetInt32());
        Assert.Equal(1, order.GetProperty("receipts").GetArrayLength());
        var events = await Get("/api/activity?pageSize=100");
        Assert.Single(events.GetProperty("items").EnumerateArray(), x => x.GetProperty("action").GetString() == "Order.Received");
    }

    [Fact]
    public async Task Concurrent_receipts_cannot_overreceive_and_concurrent_retries_have_one_effect()
    {
        await Setup();
        var catalog = await Catalog();
        var (id, line) = await ApprovedOrder(catalog);
        var results = await Task.WhenAll(Receive(id, line, 6), Receive(id, line, 6)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, x => x.IsSuccessStatusCode);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(6, await OnHand(catalog.Product, catalog.Warehouse));
        var key = Guid.NewGuid().ToString();
        var retries = await Task.WhenAll(Receive(id, line, 4, key), Receive(id, line, 4, key)).WaitAsync(TimeSpan.FromSeconds(30));
        var values = await Task.WhenAll(retries.Select(Ok));
        Assert.Single(values, x => x.GetProperty("replayed").GetBoolean());
        Assert.Single(values, x => !x.GetProperty("replayed").GetBoolean());
        Assert.Equal(10, await OnHand(catalog.Product, catalog.Warehouse));
        Assert.Equal(2, (await Get($"/api/orders/{id}")).GetProperty("receipts").GetArrayLength());
    }

    [Fact]
    public async Task Concurrent_transfers_and_issues_preserve_atomic_balances_without_negative_stock()
    {
        await Setup();
        var catalog = await Catalog();
        await Ok(await Adjust(catalog.Product, catalog.Warehouse, 10));
        var transfer = new { productId = catalog.Product, fromWarehouseId = catalog.Warehouse, toWarehouseId = catalog.OtherWarehouse,
            quantity = 7, reason = "Rebalance outbound packaging" };
        var results = await Task.WhenAll(Send(Client, "/api/inventory/transfers", transfer), Send(Client, "/api/inventory/transfers", transfer))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, x => x.IsSuccessStatusCode);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(3, await OnHand(catalog.Product, catalog.Warehouse));
        Assert.Equal(7, await OnHand(catalog.Product, catalog.OtherWarehouse));
        var outbound = Send(Client, "/api/inventory/transfers", new { productId = catalog.Product,
            fromWarehouseId = catalog.Warehouse, toWarehouseId = catalog.OtherWarehouse, quantity = 3, reason = "Move to east" });
        var inbound = Send(Client, "/api/inventory/transfers", new { productId = catalog.Product,
            fromWarehouseId = catalog.OtherWarehouse, toWarehouseId = catalog.Warehouse, quantity = 3, reason = "Move to main" });
        foreach (var response in await Task.WhenAll(outbound, inbound).WaitAsync(TimeSpan.FromSeconds(30))) await Ok(response);
        Assert.Equal(10, await OnHand(catalog.Product, catalog.Warehouse) + await OnHand(catalog.Product, catalog.OtherWarehouse));
        var issues = await Task.WhenAll(Adjust(catalog.Product, catalog.Warehouse, -2), Adjust(catalog.Product, catalog.Warehouse, -2));
        Assert.Single(issues, x => x.IsSuccessStatusCode);
        Assert.Single(issues, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await OnHand(catalog.Product, catalog.Warehouse));
        var movements = (await Get($"/api/movements?productId={catalog.Product}&pageSize=100")).GetProperty("items");
        Assert.Equal(8, movements.EnumerateArray().Sum(x => x.GetProperty("quantity").GetInt64()));
        var pairs = movements.EnumerateArray().Where(x => x.GetProperty("type").GetString()!.StartsWith("Transfer", StringComparison.Ordinal))
            .GroupBy(x => x.GetProperty("reference").GetString());
        foreach (var pair in pairs)
        {
            Assert.Equal(2, pair.Count());
            Assert.Equal(0, pair.Sum(x => x.GetProperty("quantity").GetInt64()));
        }
    }

    [Fact]
    public async Task Zero_positions_search_pagination_and_archived_stock_are_visible_in_real_reports()
    {
        await Setup();
        var catalog = await Catalog();
        var zero = await Get("/api/inventory?lowStockOnly=true&pageSize=1");
        Assert.Equal(2, zero.GetProperty("total").GetInt32());
        Assert.Equal(1, zero.GetProperty("items").GetArrayLength());
        Assert.Equal(0, zero.GetProperty("items")[0].GetProperty("onHand").GetInt64());
        Assert.Equal(0, (await Get("/api/inventory?search=unmatched")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Get($"/api/inventory?warehouseId={catalog.Warehouse}&search=carton")).GetProperty("total").GetInt32());
        await Ok(await Adjust(catalog.Product, catalog.Warehouse, 8));
        await Ok(await Send(Client, $"/api/products/{catalog.Product}", new
        { sku = "PACK-001", name = "Shipping carton", unit = "unit", reorderPoint = 5, standardCost = 2500, isActive = false }, HttpMethod.Put));
        var inventory = await Get("/api/inventory");
        Assert.Equal(1, inventory.GetProperty("total").GetInt32());
        Assert.Equal(8, inventory.GetProperty("items")[0].GetProperty("onHand").GetInt64());
        var dashboard = await Get("/api/dashboard");
        Assert.Equal(0, dashboard.GetProperty("activeProducts").GetInt32());
        Assert.Equal(20000m, dashboard.GetProperty("stockValue").GetDecimal());
        Assert.Equal(0, dashboard.GetProperty("lowStockCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Client, "/api/orders", new
        { supplierId = catalog.Supplier, warehouseId = catalog.Warehouse, expectedDate = "2026-11-10", notes = "",
            lines = new[] { new { productId = catalog.Product, quantity = 1, unitCost = 100 } } })).StatusCode);
        Assert.Equal(0, (await Get("/api/products")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Get("/api/products?includeArchived=true")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Csv_matches_applied_filters_and_neutralizes_spreadsheet_formula_text()
    {
        await Setup();
        var catalog = await Catalog();
        await Ok(await Send(Client, "/api/products", new
        { sku = "FORMULA-001", name = "=HYPERLINK(\"https://invalid.example\",\"open\")", unit = "@units", reorderPoint = 0, standardCost = 0, isActive = true }));
        var response = await Client.GetAsync($"/api/inventory/export?search=FORMULA&warehouseId={catalog.Warehouse}&lowStockOnly=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("FORMULA-001", csv);
        Assert.DoesNotContain("PACK-001", csv);
        Assert.DoesNotContain("East warehouse", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"https://invalid.example\"\"", csv);
        Assert.Contains("\"'@units\"", csv);
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Invalid_and_duplicate_catalog_input_and_null_order_lines_are_rejected_without_activity()
    {
        await Setup();
        var catalog = await Catalog();
        var activityBefore = (await Get("/api/activity")).GetProperty("total").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, "/api/products", new
        { sku = "pack-001", name = "Duplicate SKU", unit = "unit", reorderPoint = 0, standardCost = 0, isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, "/api/warehouses", new { code = "main", name = "Duplicate warehouse" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Client, "/api/products", new
        { sku = "INVALID-001", name = "Invalid cost", unit = "unit", reorderPoint = -1, standardCost = 10.001m, isActive = true })).StatusCode);
        var response = await Send(Client, "/api/orders", new
        { supplierId = catalog.Supplier, warehouseId = catalog.Warehouse, expectedDate = "2026-11-10", lines = new object?[] { null } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("lines.0", out _));
        Assert.Equal(activityBefore, (await Get("/api/activity")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/inventory?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/inventory?lowStockOnly=perhaps")).StatusCode);
    }

    [Fact]
    public async Task Rejection_is_terminal_and_cancellation_cannot_follow_a_partial_receipt()
    {
        await Setup();
        var catalog = await Catalog();
        using var manager = await CreateAccount("Manager");
        var draft = await Order(catalog);
        var id = draft.GetProperty("id").GetGuid();
        await Ok(await Send(Client, $"/api/orders/{id}/submit"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(manager, $"/api/orders/{id}/reject", new { reason = " " })).StatusCode);
        var rejected = await Ok(await Send(manager, $"/api/orders/{id}/reject", new { reason = "Supplier quote needs revision" }));
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("Supplier quote needs revision", rejected.GetProperty("decisionReason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(manager, $"/api/orders/{id}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, $"/api/orders/{id}/submit")).StatusCode);
        var other = await Order(catalog);
        var otherId = other.GetProperty("id").GetGuid();
        await Ok(await Send(Client, $"/api/orders/{otherId}/submit"));
        await Ok(await Send(manager, $"/api/orders/{otherId}/approve"));
        await Ok(await Receive(otherId, other.GetProperty("lines")[0].GetProperty("id").GetGuid(), 1));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, $"/api/orders/{otherId}/cancel", new { reason = "Already partly delivered" })).StatusCode);
    }

    [Fact]
    public async Task Activity_and_stock_ledger_are_append_only_in_the_application_context()
    {
        await Setup();
        var catalog = await Catalog();
        await Ok(await Adjust(catalog.Product, catalog.Warehouse, 10));
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var movement = await db.StockMovements.SingleAsync();
            movement.Quantity = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var activity = await db.AuditEvents.FirstAsync();
            db.AuditEvents.Remove(activity);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(10, await OnHand(catalog.Product, catalog.Warehouse));
        Assert.Equal(10, (await Get("/api/movements")).GetProperty("items")[0].GetProperty("quantity").GetInt64());
    }

    private async Task<(Guid Id, Guid Line)> ApprovedOrder((Guid Product, Guid Warehouse, Guid OtherWarehouse, Guid Supplier) catalog)
    {
        using var manager = await CreateAccount("Manager");
        var order = await Order(catalog);
        var id = order.GetProperty("id").GetGuid();
        await Ok(await Send(Client, $"/api/orders/{id}/submit"));
        await Ok(await Send(manager, $"/api/orders/{id}/approve"));
        return (id, order.GetProperty("lines")[0].GetProperty("id").GetGuid());
    }

    private Task<HttpResponseMessage> Receive(Guid orderId, Guid lineId, int quantity, string? key = null) =>
        Send(Client, $"/api/orders/{orderId}/receipts", new { notes = "Warehouse delivery", lines = new[] { new { orderLineId = lineId, quantity } } },
            idempotencyKey: key ?? Guid.NewGuid().ToString());

    private Task<HttpResponseMessage> Adjust(Guid productId, Guid warehouseId, int quantity) =>
        Send(Client, "/api/inventory/adjustments", new { productId, warehouseId, quantity, reason = "Physical count reconciliation" });

    private async Task<JsonElement> Get(string path) => await Ok(await Client.GetAsync(path));

    private async Task<long> OnHand(Guid productId, Guid warehouseId)
    {
        var inventory = await Get($"/api/inventory?warehouseId={warehouseId}&pageSize=100");
        return inventory.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("productId").GetGuid() == productId).GetProperty("onHand").GetInt64();
    }
}
