using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RetailOps.Tests;

public abstract class RetailApiTest : ApiTest
{
    protected static string Key() => Guid.NewGuid().ToString();
    protected static string TodayWib() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd");

    protected async Task<JsonElement> Get(HttpClient client, string path) => await Ok(await client.GetAsync(path));
    protected Task<JsonElement> Get(string path) => Get(Client, path);

    protected async Task<RetailFixture> Retail(int stock = 20, decimal standardCost = 40m,
        decimal retailPrice = 100m, decimal wholesalePrice = 80m, int wholesaleMinimum = 3)
    {
        var admin = await Setup();
        var product = await Ok(await Send(Client, "/api/products", new
        {
            sku = "RTEST-001", name = "Synthetic retail item", unit = "unit", reorderPoint = 2,
            standardCost, isActive = true
        }));
        var firstWarehouse = await Ok(await Send(Client, "/api/warehouses", new { code = "RTA", name = "Synthetic branch A stock" }));
        var secondWarehouse = await Ok(await Send(Client, "/api/warehouses", new { code = "RTB", name = "Synthetic branch B stock" }));
        var first = await Ok(await Send(Client, "/api/retail/branches", new
        {
            code = "RTA", name = "Synthetic branch A", warehouseId = firstWarehouse.GetProperty("id").GetGuid()
        }));
        var second = await Ok(await Send(Client, "/api/retail/branches", new
        {
            code = "RTB", name = "Synthetic branch B", warehouseId = secondWarehouse.GetProperty("id").GetGuid()
        }));
        var productId = product.GetProperty("id").GetGuid();
        var price = await Ok(await Send(Client, $"/api/retail/prices/{productId}", new
        {
            retailPrice, wholesalePrice, wholesaleMinimum, expectedVersion = 0
        }, HttpMethod.Put));
        if (stock > 0)
            await Ok(await Send(Client, "/api/inventory/adjustments", new
            {
                productId, warehouseId = firstWarehouse.GetProperty("id").GetGuid(), quantity = stock,
                reason = "Synthetic retail opening stock"
            }));
        var supplier = await Ok(await Send(Client, "/api/suppliers", new
        {
            name = "Synthetic retail supplier", email = "supplier@retailops.test", phone = "", leadTimeDays = 1
        }));
        var cashier = await CreateAccount("Cashier", "cashier-a@retailops.test");
        var other = await CreateAccount("Cashier", "cashier-b@retailops.test");
        var manager = await CreateAccount("Manager", "retail-manager@retailops.test");
        var cashierUser = await Get(cashier, "/api/auth/me");
        var otherUser = await Get(other, "/api/auth/me");
        var cashierId = cashierUser.GetProperty("id").GetGuid();
        var otherId = otherUser.GetProperty("id").GetGuid();
        var branchId = first.GetProperty("id").GetGuid();
        var otherBranchId = second.GetProperty("id").GetGuid();
        await Assign(cashierId, branchId);
        await Assign(otherId, otherBranchId);
        var shift = await OpenShift(cashier, branchId, 1000m);
        return new RetailFixture
        {
            Product = productId, Warehouse = firstWarehouse.GetProperty("id").GetGuid(),
            OtherWarehouse = secondWarehouse.GetProperty("id").GetGuid(), Supplier = supplier.GetProperty("id").GetGuid(),
            Branch = branchId, OtherBranch = otherBranchId, Shift = shift.GetProperty("id").GetGuid(),
            CashierId = cashierId, OtherCashierId = otherId, AdminId = admin.GetProperty("id").GetGuid(),
            Cashier = cashier, OtherCashier = other, Manager = manager, RetailPrice = retailPrice,
            WholesalePrice = wholesalePrice, WholesaleMinimum = wholesaleMinimum,
            PriceVersion = price.GetProperty("version").GetInt64(), StandardCost = standardCost
        };
    }

    protected Task Assign(Guid userId, Guid branchId) => Assign(Client, userId, branchId);
    protected async Task Assign(HttpClient client, Guid userId, Guid branchId)
    {
        using var response = await Send(client, "/api/retail/assignments", new { userId, branchId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    protected async Task<HttpClient> Login(string email)
    {
        var client = NewClient();
        await Ok(await Send(client, "/api/auth/login", new { email, password = Password }));
        return client;
    }

    protected async Task<JsonElement> OpenShift(HttpClient client, Guid branchId, decimal openingCash = 0m,
        string? key = null) => await Ok(await Send(client, "/api/retail/shifts", new { branchId, openingCash }, idempotencyKey: key ?? Key()));

    protected SaleCommand Command(RetailFixture fixture, int quantity = 1, decimal? tendered = null,
        Guid? shift = null, Guid? branch = null, string tier = "Retail", DateTimeOffset? clientCreatedAt = null)
        => new(branch ?? fixture.Branch, shift ?? fixture.Shift, tier,
            clientCreatedAt ?? DateTimeOffset.Parse("2020-01-02T03:04:05Z"),
            [new SaleCommandLine(fixture.Product, quantity,
                tier == "Wholesale" ? fixture.WholesalePrice : fixture.RetailPrice, fixture.PriceVersion)],
            tendered ?? quantity * (tier == "Wholesale" ? fixture.WholesalePrice : fixture.RetailPrice));

    protected Task<HttpResponseMessage> Sell(HttpClient client, SaleCommand command, string? key = null)
        => Send(client, "/api/retail/sales", command, idempotencyKey: key ?? Key());

    protected async Task<JsonElement> Report(Guid? branch = null, string? from = null, string? to = null)
        => await Get($"/api/retail/reports?from={from ?? TodayWib()}&to={to ?? TodayWib()}" + (branch.HasValue ? $"&branchId={branch}" : ""));

    protected async Task<long> Stock(Guid product, Guid warehouse)
    {
        var inventory = await Get($"/api/inventory?productId={product}&warehouseId={warehouse}&pageSize=100");
        var rows = inventory.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("productId").GetGuid() == product && item.GetProperty("warehouseId").GetGuid() == warehouse).ToArray();
        return rows.Length == 0 ? 0 : Assert.Single(rows).GetProperty("onHand").GetInt64();
    }

    protected static async Task AssertDenied(HttpResponseMessage response)
    {
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.ToString() ?? "");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(problem.TryGetProperty("password", out _));
    }

    protected sealed record SaleCommandLine(Guid ProductId, int Quantity, decimal UnitPrice, long PriceVersion);
    protected sealed record SaleCommand(Guid BranchId, Guid ShiftId, string PriceTier,
        DateTimeOffset ClientCreatedAt, List<SaleCommandLine> Lines, decimal CashTendered);

    protected sealed class RetailFixture : IDisposable
    {
        public Guid Product { get; init; }
        public Guid Warehouse { get; init; }
        public Guid OtherWarehouse { get; init; }
        public Guid Supplier { get; init; }
        public Guid Branch { get; init; }
        public Guid OtherBranch { get; init; }
        public Guid Shift { get; init; }
        public Guid CashierId { get; init; }
        public Guid OtherCashierId { get; init; }
        public Guid AdminId { get; init; }
        public required HttpClient Cashier { get; init; }
        public required HttpClient OtherCashier { get; init; }
        public required HttpClient Manager { get; init; }
        public decimal RetailPrice { get; init; }
        public decimal WholesalePrice { get; init; }
        public int WholesaleMinimum { get; init; }
        public long PriceVersion { get; init; }
        public decimal StandardCost { get; init; }
        public void Dispose() { Cashier.Dispose(); OtherCashier.Dispose(); Manager.Dispose(); }
    }
}
