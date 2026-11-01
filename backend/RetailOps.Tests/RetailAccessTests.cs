using System.Net;
using System.Text.Json;
using Xunit;

namespace RetailOps.Tests;

public sealed class RetailAccessTests : RetailApiTest
{
    [Fact]
    public async Task Cashier_sees_assigned_catalog_without_costs_and_cannot_reach_inherited_business_routes()
    {
        using var f = await Retail();
        var branches = await Get(f.Cashier, "/api/retail/branches");
        Assert.Equal(f.Branch, Assert.Single(branches.EnumerateArray()).GetProperty("id").GetGuid());
        var catalog = await Get(f.Cashier, $"/api/retail/catalog?branchId={f.Branch}");
        var product = Assert.Single(catalog.GetProperty("products").EnumerateArray());
        Assert.Equal(20, product.GetProperty("onHand").GetInt64());
        Assert.Equal(100m, product.GetProperty("retailPrice").GetDecimal());
        Assert.False(product.TryGetProperty("standardCost", out _));
        Assert.False(product.TryGetProperty("cost", out _));
        Assert.False(product.TryGetProperty("stockValue", out _));
        await AssertDenied(await f.Cashier.GetAsync($"/api/retail/catalog?branchId={f.OtherBranch}"));
        foreach (var path in new[]
        {
            "/api/products", "/api/suppliers", "/api/warehouses", "/api/orders", "/api/inventory",
            "/api/inventory/export", "/api/movements", "/api/dashboard", "/api/activity", "/api/users",
            "/api/retail/prices", "/api/retail/assignments", "/api/retail/journals",
            $"/api/retail/reports?from={TodayWib()}&to={TodayWib()}"
        })
            Assert.Equal(HttpStatusCode.Forbidden, (await f.Cashier.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(f.Cashier, "/api/inventory/adjustments", new
        { productId = f.Product, warehouseId = f.Warehouse, quantity = 1, reason = "Blocked cashier adjustment" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(f.Cashier, "/api/orders", new
        {
            supplierId = f.Supplier, warehouseId = f.Warehouse, expectedDate = TodayWib(), notes = "Blocked cashier procurement",
            lines = new[] { new { productId = f.Product, quantity = 1, unitCost = 40m } }
        })).StatusCode);
        Assert.Equal(20, await Stock(f.Product, f.Warehouse));
    }

    [Fact]
    public async Task Receipt_and_shift_lists_are_owner_scoped_even_when_cashiers_share_a_branch()
    {
        using var f = await Retail();
        await Assign(f.OtherCashierId, f.Branch);
        var otherShift = await OpenShift(f.OtherCashier, f.Branch);
        var own = await Ok(await Sell(f.Cashier, Command(f)));
        var other = await Ok(await Sell(f.OtherCashier, Command(f, 2, shift: otherShift.GetProperty("id").GetGuid())));
        var receipts = await Get(f.Cashier, $"/api/retail/sales?branchId={f.Branch}");
        Assert.Equal(own.GetProperty("id").GetGuid(), Assert.Single(receipts.EnumerateArray()).GetProperty("id").GetGuid());
        await AssertDenied(await f.Cashier.GetAsync($"/api/retail/sales/{other.GetProperty("id").GetGuid()}"));
        var shifts = await Get(f.Cashier, $"/api/retail/shifts?branchId={f.Branch}");
        Assert.Equal(f.Shift, Assert.Single(shifts.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(2, (await Get(Client, $"/api/retail/sales?branchId={f.Branch}")).GetArrayLength());
    }

    [Fact]
    public async Task Revoked_assignment_blocks_replay_and_receipt_access_until_permission_is_restored()
    {
        using var f = await Retail();
        var command = Command(f);
        var key = Key();
        var original = await Ok(await Sell(f.Cashier, command, key));
        var saleId = original.GetProperty("id").GetGuid();
        var beforeJournals = (await Get("/api/retail/journals")).GetArrayLength();
        using var revoked = await Send(Client, $"/api/retail/assignments/{f.CashierId}/{f.Branch}", method: HttpMethod.Delete);
        Assert.True(revoked.IsSuccessStatusCode);
        await AssertDenied(await Sell(f.Cashier, command, key));
        await AssertDenied(await f.Cashier.GetAsync($"/api/retail/sales/{saleId}"));
        Assert.Equal(19, await Stock(f.Product, f.Warehouse));
        Assert.Equal(beforeJournals, (await Get("/api/retail/journals")).GetArrayLength());
        await Assign(f.CashierId, f.Branch);
        var replay = await Ok(await Sell(f.Cashier, command, key));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(saleId, replay.GetProperty("id").GetGuid());
        Assert.Equal(19, await Stock(f.Product, f.Warehouse));
    }

    [Fact]
    public async Task Cashier_cannot_use_or_close_another_owners_shift_or_authorize_returns()
    {
        using var f = await Retail();
        await Assign(f.OtherCashierId, f.Branch);
        var other = await OpenShift(f.OtherCashier, f.Branch);
        var otherId = other.GetProperty("id").GetGuid();
        await AssertDenied(await Sell(f.Cashier, Command(f, shift: otherId)));
        await AssertDenied(await Send(f.Cashier, $"/api/retail/shifts/{otherId}/close", new { countedCash = 0m }, idempotencyKey: Key()));
        var sale = await Ok(await Sell(f.Cashier, Command(f)));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(f.Cashier, $"/api/retail/sales/{sale.GetProperty("id").GetGuid()}/returns", new
        {
            shiftId = f.Shift, reason = "Blocked cashier refund",
            lines = new[] { new { saleLineId = sale.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 1 } }
        }, idempotencyKey: Key())).StatusCode);
        Assert.Equal(19, await Stock(f.Product, f.Warehouse));
    }

    [Fact]
    public async Task Branch_mapping_requires_empty_warehouse_and_one_branch_per_warehouse()
    {
        using var f = await Retail();
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, "/api/retail/branches", new
        { code = "DUP", name = "Duplicate warehouse mapping", warehouseId = f.Warehouse })).StatusCode);
        var unassigned = await Ok(await Send(Client, "/api/warehouses", new { code = "USED", name = "Unmapped stocked warehouse" }));
        var warehouseId = unassigned.GetProperty("id").GetGuid();
        await Ok(await Send(Client, "/api/inventory/adjustments", new
        { productId = f.Product, warehouseId, quantity = 1, reason = "Stock before branch mapping" }));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, "/api/retail/branches", new
        { code = "USED", name = "Must not map populated stock", warehouseId })).StatusCode);
        Assert.Equal(2, (await Get("/api/retail/branches")).GetArrayLength());
        Assert.Equal(1, await Stock(f.Product, warehouseId));
    }

    [Fact]
    public async Task Price_rules_and_optimistic_version_reject_invalid_updates_without_changing_catalog()
    {
        using var f = await Retail();
        object[] invalid =
        [
            new { retailPrice = 0m, wholesalePrice = 0m, wholesaleMinimum = 3, expectedVersion = f.PriceVersion },
            new { retailPrice = 100.001m, wholesalePrice = 80m, wholesaleMinimum = 3, expectedVersion = f.PriceVersion },
            new { retailPrice = 100m, wholesalePrice = 101m, wholesaleMinimum = 3, expectedVersion = f.PriceVersion },
            new { retailPrice = 100m, wholesalePrice = 80m, wholesaleMinimum = 1, expectedVersion = f.PriceVersion }
        ];
        foreach (var update in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(Client, $"/api/retail/prices/{f.Product}", update, HttpMethod.Put)).StatusCode);
        var valid = new { retailPrice = 110m, wholesalePrice = 90m, wholesaleMinimum = 3, expectedVersion = f.PriceVersion };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(f.Manager, $"/api/retail/prices/{f.Product}", valid, HttpMethod.Put)).StatusCode);
        var updated = await Ok(await Send(Client, $"/api/retail/prices/{f.Product}", valid, HttpMethod.Put));
        Assert.True(updated.GetProperty("version").GetInt64() > f.PriceVersion);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, $"/api/retail/prices/{f.Product}", valid, HttpMethod.Put)).StatusCode);
        var catalog = await Get(f.Cashier, $"/api/retail/catalog?branchId={f.Branch}");
        Assert.Equal(110m, Assert.Single(catalog.GetProperty("products").EnumerateArray()).GetProperty("retailPrice").GetDecimal());
        Assert.Equal(20, await Stock(f.Product, f.Warehouse));
    }
}
