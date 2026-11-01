using System.Net;
using System.Text.Json;
using Xunit;

namespace RetailOps.Tests;

public sealed class RetailMutationTests : RetailApiTest
{
    [Fact]
    public async Task Parallel_last_unit_sales_have_one_committed_receipt_without_negative_stock()
    {
        using var f = await Retail(stock: 1);
        await Assign(f.OtherCashierId, f.Branch);
        var secondShift = await OpenShift(f.OtherCashier, f.Branch);
        var responses = await Task.WhenAll(
            Sell(f.Cashier, Command(f)),
            Sell(f.OtherCashier, Command(f, shift: secondShift.GetProperty("id").GetGuid())))
            .WaitAsync(TimeSpan.FromSeconds(30));
        var accepted = await Ok(Assert.Single(responses, response => response.IsSuccessStatusCode));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(0, await Stock(f.Product, f.Warehouse));
        Assert.Equal(1, (await Get($"/api/retail/sales?branchId={f.Branch}")).GetArrayLength());
        Assert.Equal(1, await JournalCount(accepted.GetProperty("id").GetGuid()));
        var report = await Report();
        Assert.Equal(1, report.GetProperty("salesCount").GetInt64());
        Assert.Equal(100m, report.GetProperty("grossSales").GetDecimal());
        Assert.True(report.GetProperty("journalBalanced").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_same_owner_retries_from_separate_sessions_commit_one_sale_and_journal()
    {
        using var f = await Retail();
        using var secondSession = await Login("cashier-a@retailops.test");
        var key = Key();
        var command = Command(f, 2, tendered: 250m);
        var responses = await Task.WhenAll(Sell(f.Cashier, command, key), Sell(secondSession, command, key))
            .WaitAsync(TimeSpan.FromSeconds(30));
        var values = await Task.WhenAll(responses.Select(Ok));
        Assert.Equal(values[0].GetProperty("id").GetGuid(), values[1].GetProperty("id").GetGuid());
        Assert.Single(values, value => value.GetProperty("replayed").GetBoolean());
        Assert.Single(values, value => !value.GetProperty("replayed").GetBoolean());
        Assert.Equal(200m, values[0].GetProperty("total").GetDecimal());
        Assert.Equal(50m, values[0].GetProperty("change").GetDecimal());
        Assert.Equal(18, await Stock(f.Product, f.Warehouse));
        Assert.Equal(1, await JournalCount(values[0].GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Reused_sale_key_rejects_changed_payload_owner_and_provenance()
    {
        using var f = await Retail();
        await Assign(f.OtherCashierId, f.Branch);
        var key = Key();
        var command = Command(f);
        var original = await Ok(await Sell(f.Cashier, command, key));
        Assert.Equal(HttpStatusCode.Conflict, (await Sell(f.Cashier, Command(f, 2), key)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Sell(f.Cashier,
            command with { ClientCreatedAt = command.ClientCreatedAt.AddSeconds(1) }, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Sell(f.OtherCashier, command, key)).StatusCode);
        Assert.Equal(19, await Stock(f.Product, f.Warehouse));
        Assert.Equal(1, await JournalCount(original.GetProperty("id").GetGuid()));
        var receipt = await Get(f.Cashier, $"/api/retail/sales/{original.GetProperty("id").GetGuid()}");
        Assert.Equal(f.CashierId, receipt.GetProperty("actorId").GetGuid());
        Assert.Equal(command.ClientCreatedAt, receipt.GetProperty("clientCreatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Canonical_product_order_replays_the_same_command_without_another_effect()
    {
        using var f = await Retail();
        var second = await Ok(await Send(Client, "/api/products", new
        { sku = "RTEST-002", name = "Second synthetic item", unit = "unit", reorderPoint = 0, standardCost = 10m, isActive = true }));
        var productId = second.GetProperty("id").GetGuid();
        var price = await Ok(await Send(Client, $"/api/retail/prices/{productId}", new
        { retailPrice = 30m, wholesalePrice = 25m, wholesaleMinimum = 3, expectedVersion = 0 }, HttpMethod.Put));
        await Ok(await Send(Client, "/api/inventory/adjustments", new
        { productId, warehouseId = f.Warehouse, quantity = 5, reason = "Second synthetic item opening stock" }));
        var command = Command(f) with
        {
            Lines = [new(f.Product, 1, 100m, f.PriceVersion), new(productId, 2, 30m, price.GetProperty("version").GetInt64())],
            CashTendered = 200m
        };
        var key = Key();
        var original = await Ok(await Sell(f.Cashier, command, key));
        var replay = await Ok(await Sell(f.Cashier, command with { Lines = command.Lines.AsEnumerable().Reverse().ToList() }, key));
        Assert.Equal(original.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(160m, replay.GetProperty("total").GetDecimal());
        Assert.Equal(40m, replay.GetProperty("change").GetDecimal());
        Assert.Equal(19, await Stock(f.Product, f.Warehouse));
        Assert.Equal(3, await Stock(productId, f.Warehouse));
    }

    [Fact]
    public async Task Accepted_sale_replays_after_shift_closure_and_later_price_changes()
    {
        using var f = await Retail();
        var key = Key();
        var command = Command(f, 2, tendered: 250m);
        var original = await Ok(await Sell(f.Cashier, command, key));
        var closeKey = Key();
        var closed = await Ok(await Send(f.Cashier, $"/api/retail/shifts/{f.Shift}/close", new { countedCash = 1200m }, idempotencyKey: closeKey));
        Assert.Equal(1200m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(0m, closed.GetProperty("variance").GetDecimal());
        await Ok(await Send(Client, $"/api/retail/prices/{f.Product}", new
        { retailPrice = 120m, wholesalePrice = 90m, wholesaleMinimum = 3, expectedVersion = f.PriceVersion }, HttpMethod.Put));
        var replay = await Ok(await Sell(f.Cashier, command, key));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(original.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        Assert.Equal(200m, replay.GetProperty("total").GetDecimal());
        var repeatedClose = await Ok(await Send(f.Cashier, $"/api/retail/shifts/{f.Shift}/close", new { countedCash = 1200m }, idempotencyKey: closeKey));
        Assert.Equal(closed.GetProperty("closedAt").GetDateTimeOffset(), repeatedClose.GetProperty("closedAt").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(f.Cashier, $"/api/retail/shifts/{f.Shift}/close", new { countedCash = 1199m }, idempotencyKey: closeKey)).StatusCode);
        Assert.Equal(18, await Stock(f.Product, f.Warehouse));
        Assert.Equal(1, await JournalCount(original.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Stale_catalog_requires_explicit_new_price_and_key_without_silent_repricing()
    {
        using var f = await Retail();
        var old = Command(f);
        var key = Key();
        var price = await Ok(await Send(Client, $"/api/retail/prices/{f.Product}", new
        { retailPrice = 110m, wholesalePrice = 90m, wholesaleMinimum = 3, expectedVersion = f.PriceVersion }, HttpMethod.Put));
        Assert.Equal(HttpStatusCode.Conflict, (await Sell(f.Cashier, old, key)).StatusCode);
        Assert.Equal(20, await Stock(f.Product, f.Warehouse));
        Assert.Empty((await Get($"/api/retail/sales?branchId={f.Branch}")).EnumerateArray());
        var reviewed = old with { Lines = [new(f.Product, 1, 110m, price.GetProperty("version").GetInt64())], CashTendered = 110m };
        var accepted = await Ok(await Sell(f.Cashier, reviewed, Key()));
        Assert.Equal(110m, accepted.GetProperty("total").GetDecimal());
        Assert.Equal(19, await Stock(f.Product, f.Warehouse));
    }

    [Fact]
    public async Task Wholesale_prices_require_the_per_product_minimum_and_compute_cash_on_the_server()
    {
        using var f = await Retail();
        Assert.Equal(HttpStatusCode.Conflict, (await Sell(f.Cashier, Command(f, 2, tier: "Wholesale"))).StatusCode);
        var sale = await Ok(await Sell(f.Cashier, Command(f, 3, tendered: 300m, tier: "Wholesale")));
        Assert.Equal("Wholesale", sale.GetProperty("priceTier").GetString());
        Assert.Equal(240m, sale.GetProperty("total").GetDecimal());
        Assert.Equal(60m, sale.GetProperty("change").GetDecimal());
        Assert.Equal(80m, sale.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(17, await Stock(f.Product, f.Warehouse));
    }

    [Fact]
    public async Task Missing_key_invalid_lines_and_underpayment_leave_inventory_and_financials_unchanged()
    {
        using var f = await Retail();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(f.Cashier, "/api/retail/sales", Command(f))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Sell(f.Cashier, Command(f), "not-a-uuid")).StatusCode);
        var command = Command(f);
        var invalid = new[]
        {
            command with { Lines = [] },
            command with { Lines = [new(f.Product, 0, 100m, f.PriceVersion)] },
            command with { Lines = [new(f.Product, -1, 100m, f.PriceVersion)] },
            command with { Lines = [new(f.Product, 1, 100m, f.PriceVersion), new(f.Product, 1, 100m, f.PriceVersion)], CashTendered = 200m },
            command with { CashTendered = 99m },
            command with { CashTendered = 100.001m }
        };
        foreach (var item in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await Sell(f.Cashier, item)).StatusCode);
        Assert.Equal(20, await Stock(f.Product, f.Warehouse));
        Assert.Equal(0, (await Report()).GetProperty("salesCount").GetInt64());
        Assert.Single((await Get("/api/retail/journals")).EnumerateArray());
    }

    [Fact]
    public async Task Client_timestamp_is_retained_as_provenance_and_cannot_backdate_accounting()
    {
        using var f = await Retail();
        var before = DateTimeOffset.UtcNow;
        var command = Command(f, clientCreatedAt: DateTimeOffset.Parse("2000-01-02T01:02:03Z"));
        var sale = await Ok(await Sell(f.Cashier, command));
        Assert.Equal(command.ClientCreatedAt, sale.GetProperty("clientCreatedAt").GetDateTimeOffset());
        Assert.InRange(sale.GetProperty("createdAt").GetDateTimeOffset(), before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(1, (await Report()).GetProperty("salesCount").GetInt64());
        var historical = await Report(from: "2000-01-02", to: "2000-01-02");
        Assert.Equal(0, historical.GetProperty("salesCount").GetInt64());
        Assert.Equal(0m, historical.GetProperty("grossSales").GetDecimal());
    }

    [Fact]
    public async Task Shift_close_serializes_with_a_sale_and_does_not_post_drawer_variance_to_the_GL()
    {
        using var f = await Retail(stock: 1);
        using var otherSession = await Login("cashier-a@retailops.test");
        var closeKey = Key();
        var tasks = await Task.WhenAll(
            Sell(f.Cashier, Command(f)),
            Send(otherSession, $"/api/retail/shifts/{f.Shift}/close", new { countedCash = 1100m }, idempotencyKey: closeKey))
            .WaitAsync(TimeSpan.FromSeconds(30));
        var closed = await Ok(tasks[1]);
        var sold = tasks[0].IsSuccessStatusCode;
        if (!sold) Assert.Equal(HttpStatusCode.Conflict, tasks[0].StatusCode);
        Assert.Equal(sold ? 1100m : 1000m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(sold ? 0m : 100m, closed.GetProperty("variance").GetDecimal());
        Assert.Equal(sold ? 0 : 1, await Stock(f.Product, f.Warehouse));
        Assert.Equal(sold ? 2 : 1, (await Get("/api/retail/journals")).GetArrayLength());
        Assert.Equal(sold ? 1 : 0, (await Report()).GetProperty("salesCount").GetInt64());
    }

    [Fact]
    public async Task Only_one_open_shift_is_created_when_the_same_user_opens_two_branches_concurrently()
    {
        using var f = await Retail();
        await Ok(await Send(f.Cashier, $"/api/retail/shifts/{f.Shift}/close", new { countedCash = 1000m }, idempotencyKey: Key()));
        await Assign(f.CashierId, f.OtherBranch);
        using var secondSession = await Login("cashier-a@retailops.test");
        var results = await Task.WhenAll(
            Send(f.Cashier, "/api/retail/shifts", new { branchId = f.Branch, openingCash = 0m }, idempotencyKey: Key()),
            Send(secondSession, "/api/retail/shifts", new { branchId = f.OtherBranch, openingCash = 0m }, idempotencyKey: Key()))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, result => result.IsSuccessStatusCode);
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);
        var first = await Get(f.Cashier, $"/api/retail/shifts?branchId={f.Branch}");
        var second = await Get(f.Cashier, $"/api/retail/shifts?branchId={f.OtherBranch}");
        Assert.Equal(1, first.EnumerateArray().Concat(second.EnumerateArray()).Count(item => item.GetProperty("status").GetString() == "Open"));
    }

    [Fact]
    public async Task Concurrent_partial_returns_cannot_refund_more_than_the_original_quantity()
    {
        using var f = await Retail(stock: 5);
        var sale = await Ok(await Sell(f.Cashier, Command(f, 3)));
        var saleId = sale.GetProperty("id").GetGuid();
        var lineId = sale.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var shift = await OpenShift(f.Manager, f.Branch, 1000m);
        using var secondManager = await CreateAccount("Manager", "second-return-manager@retailops.test");
        var otherShift = await OpenShift(secondManager, f.Branch, 1000m);
        var payload = new { shiftId = shift.GetProperty("id").GetGuid(), reason = "Synthetic partial return", lines = new[] { new { saleLineId = lineId, quantity = 2 } } };
        var responses = await Task.WhenAll(
            Send(f.Manager, $"/api/retail/sales/{saleId}/returns", payload, idempotencyKey: Key()),
            Send(secondManager, $"/api/retail/sales/{saleId}/returns", new
            {
                shiftId = otherShift.GetProperty("id").GetGuid(), payload.reason, payload.lines
            }, idempotencyKey: Key()))
            .WaitAsync(TimeSpan.FromSeconds(30));
        await Ok(Assert.Single(responses, response => response.IsSuccessStatusCode));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(4, await Stock(f.Product, f.Warehouse));
        var receipt = await Get($"/api/retail/sales/{saleId}");
        Assert.Equal(2, receipt.GetProperty("lines")[0].GetProperty("returnedQuantity").GetInt32());
        var report = await Report();
        Assert.Equal(300m, report.GetProperty("grossSales").GetDecimal());
        Assert.Equal(200m, report.GetProperty("refunds").GetDecimal());
        Assert.Equal(40m, report.GetProperty("costOfGoodsSold").GetDecimal());
    }

    [Fact]
    public async Task Same_return_key_retries_have_one_effect_and_changed_quantities_conflict()
    {
        using var f = await Retail(stock: 5);
        var sale = await Ok(await Sell(f.Cashier, Command(f, 3)));
        var saleId = sale.GetProperty("id").GetGuid();
        var lineId = sale.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var shift = await OpenShift(f.Manager, f.Branch, 1000m);
        var payload = new { shiftId = shift.GetProperty("id").GetGuid(), reason = "Synthetic return replay", lines = new[] { new { saleLineId = lineId, quantity = 1 } } };
        var key = Key();
        using var secondSession = await Login("retail-manager@retailops.test");
        var values = await Task.WhenAll((await Task.WhenAll(
            Send(f.Manager, $"/api/retail/sales/{saleId}/returns", payload, idempotencyKey: key),
            Send(secondSession, $"/api/retail/sales/{saleId}/returns", payload, idempotencyKey: key)).WaitAsync(TimeSpan.FromSeconds(30)))
            .Select(Ok));
        Assert.Equal(values[0].GetProperty("id").GetGuid(), values[1].GetProperty("id").GetGuid());
        Assert.Single(values, value => value.GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(f.Manager, $"/api/retail/sales/{saleId}/returns", new
        { payload.shiftId, payload.reason, lines = new[] { new { saleLineId = lineId, quantity = 2 } } }, idempotencyKey: key)).StatusCode);
        Assert.Equal(3, await Stock(f.Product, f.Warehouse));
        Assert.Equal(1, (await Get($"/api/retail/sales/{saleId}")).GetProperty("lines")[0].GetProperty("returnedQuantity").GetInt32());
        Assert.Equal(1, await JournalCount(values[0].GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Return_after_original_shift_closed_uses_the_refunding_operators_open_drawer()
    {
        using var f = await Retail();
        var sale = await Ok(await Sell(f.Cashier, Command(f, 3)));
        var openSaleShift = Assert.Single((await Get(f.Cashier, $"/api/retail/shifts?branchId={f.Branch}")).EnumerateArray());
        Assert.Equal(1300m, openSaleShift.GetProperty("expectedCash").GetDecimal());
        await Ok(await Send(f.Cashier, $"/api/retail/shifts/{f.Shift}/close", new { countedCash = 1300m }, idempotencyKey: Key()));
        var shift = await OpenShift(f.Manager, f.Branch, 1000m);
        var managerShift = shift.GetProperty("id").GetGuid();
        await Ok(await Send(f.Manager, $"/api/retail/sales/{sale.GetProperty("id").GetGuid()}/returns", new
        {
            shiftId = managerShift, reason = "Synthetic return after original drawer closed",
            lines = new[] { new { saleLineId = sale.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 1 } }
        }, idempotencyKey: Key()));
        var openRefundShift = (await Get(f.Manager, $"/api/retail/shifts?branchId={f.Branch}")).EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == managerShift);
        Assert.Equal(900m, openRefundShift.GetProperty("expectedCash").GetDecimal());
        var closed = await Ok(await Send(f.Manager, $"/api/retail/shifts/{managerShift}/close", new { countedCash = 900m }, idempotencyKey: Key()));
        Assert.Equal(900m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(0m, closed.GetProperty("variance").GetDecimal());
        var old = Assert.Single((await Get(f.Cashier, $"/api/retail/shifts?branchId={f.Branch}")).EnumerateArray());
        Assert.Equal(1300m, old.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(0m, old.GetProperty("variance").GetDecimal());
    }

    private async Task<int> JournalCount(Guid sourceId) => (await Get("/api/retail/journals")).EnumerateArray()
        .Count(journal => journal.GetProperty("sourceId").GetGuid() == sourceId);
}
