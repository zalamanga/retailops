using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RetailOps.Api.Data;
using RetailOps.Api.Security;
using Xunit;

namespace RetailOps.Tests;

public sealed class ConsistencyEdgeTests : ApiTest
{
    [Fact]
    public async Task Liveness_ignores_session_cookies_when_the_database_is_unavailable()
    {
        var unavailable = new NpgsqlConnectionStringBuilder(DatabaseConnection)
        { Host = "127.0.0.1", Port = 1, Timeout = 1, CommandTimeout = 1, Pooling = false };
        await using var factory = new UnavailableDatabaseFactory(unavailable.ConnectionString);
        using var client = factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionAuthentication.CookieName}={new string('a', 43)}");
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("live", (await live.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Invalid_transfer_source_reports_the_actual_request_field_and_writes_nothing()
    {
        await Setup();
        var catalog = await Catalog();
        var before = await Client.GetFromJsonAsync<JsonElement>("/api/activity");
        var response = await Send(Client, "/api/inventory/transfers", new
        {
            productId = catalog.Product, fromWarehouseId = Guid.NewGuid(), toWarehouseId = catalog.OtherWarehouse,
            quantity = 1, reason = "Allocate cartons to east"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("fromWarehouseId", out _));
        Assert.False(problem.GetProperty("errors").TryGetProperty("warehouseId", out _));
        var after = await Client.GetFromJsonAsync<JsonElement>("/api/activity");
        Assert.Equal(before.GetProperty("total").GetInt32(), after.GetProperty("total").GetInt32());
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.StockMovements.CountAsync());
        Assert.Equal(0, await db.InventoryBalances.CountAsync());
    }

    [Fact]
    public async Task New_order_waits_for_an_earlier_product_archive_and_cannot_persist_an_archived_line()
    {
        await Setup();
        var catalog = await Catalog();
        await using var gate = new NpgsqlConnection(DatabaseConnection);
        await gate.OpenAsync();
        await using var transaction = await gate.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", gate, transaction))
        {
            command.Parameters.AddWithValue("key", $"product:{catalog.Product:N}");
            await command.ExecuteNonQueryAsync();
        }
        await using var observer = new NpgsqlConnection(DatabaseConnection);
        await observer.OpenAsync();
        var archive = Send(Client, $"/api/products/{catalog.Product}", new
        { sku = "PACK-001", name = "Shipping carton", unit = "unit", reorderPoint = 5, standardCost = 2500, isActive = false }, HttpMethod.Put);
        Assert.True(await WaitForAdvisoryWaiters(observer, 1, archive), "The archive must hold the shared product boundary before changing product state.");
        var create = Send(Client, "/api/orders", new
        {
            supplierId = catalog.Supplier, warehouseId = catalog.Warehouse,
            expectedDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"), notes = "Concurrent replenishment request",
            lines = new[] { new { productId = catalog.Product, quantity = 2, unitCost = 2500 } }
        });
        Assert.True(await WaitForAdvisoryWaiters(observer, 2, create), "New orders must wait before reading a product that is being archived.");
        await transaction.CommitAsync();
        await Ok(await archive);
        var rejected = await create.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("lines.0.productId", out _));
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False((await db.Products.SingleAsync()).IsActive);
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.OrderLines.CountAsync());
        Assert.Equal(0, await db.AuditEvents.CountAsync(x => x.Action == "Order.Created"));
    }

    private static async Task<bool> WaitForAdvisoryWaiters(NpgsqlConnection observer, int expected, Task request)
    {
        var timer = Stopwatch.StartNew();
        while (!request.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event_type = 'Lock'
                  AND wait_event = 'advisory' AND query LIKE '%pg_advisory_xact_lock%'
                """, observer);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) >= expected) return true;
            await Task.Delay(25);
        }
        return false;
    }

    private sealed class UnavailableDatabaseFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Database", connection);
            builder.UseSetting("Security:SecureCookies", "false");
        }
    }
}
