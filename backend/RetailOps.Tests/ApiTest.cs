using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RetailOps.Api.Data;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RetailOps.Tests;

public abstract class ApiTest : IAsyncLifetime
{
    protected const string Password = "WarehouseAccess947!";
    protected HttpClient Client = null!;
    protected WebApplicationFactory<Program> Factory = null!;
    protected string DatabaseConnection = "";
    private string adminConnection = "";
    private string databaseName = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("RETAILOPS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Set RETAILOPS_TEST_CONNECTION to an isolated PostgreSQL server before integration tests.");
        var connection = new NpgsqlConnectionStringBuilder(configured);
        if (!(connection.Database ?? "").StartsWith("retailops_test", StringComparison.Ordinal))
            throw new InvalidOperationException("The test database name must start with retailops_test.");
        databaseName = "retailops_test_" + Guid.NewGuid().ToString("N");
        connection.Database = "postgres";
        adminConnection = connection.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await command.ExecuteNonQueryAsync();
        }
        connection.Database = databaseName;
        DatabaseConnection = connection.ConnectionString;
        Factory = new WorkspaceFactory(DatabaseConnection);
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        Client = NewClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null) await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (databaseName.Length > 0)
        {
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }

    protected HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost")
    });

    protected static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null,
        HttpMethod? method = null, string? idempotencyKey = null)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    protected static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected successful response, got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    protected async Task<JsonElement> Setup(HttpClient? client = null) => await Ok(await Send(client ?? Client, "/api/auth/setup", new
    {
        organizationName = "RetailOps Verification", fullName = "Workspace Administrator",
        email = "admin@retailops.test", password = Password
    }));

    protected async Task<HttpClient> CreateAccount(string role, string email = "manager@retailops.test")
    {
        await Ok(await Send(Client, "/api/users", new { fullName = role + " Account", email, password = Password, role }));
        var client = NewClient();
        await Ok(await Send(client, "/api/auth/login", new { email, password = Password }));
        return client;
    }

    protected async Task<(Guid Product, Guid Warehouse, Guid OtherWarehouse, Guid Supplier)> Catalog()
    {
        var product = await Ok(await Send(Client, "/api/products", new
        { sku = "PACK-001", name = "Shipping carton", unit = "unit", reorderPoint = 5, standardCost = 2500, isActive = true }));
        var warehouse = await Ok(await Send(Client, "/api/warehouses", new { code = "MAIN", name = "Main warehouse" }));
        var other = await Ok(await Send(Client, "/api/warehouses", new { code = "EAST", name = "East warehouse" }));
        var supplier = await Ok(await Send(Client, "/api/suppliers", new
        { name = "Packaging supplier", email = "supply@retailops.test", phone = "021123456", leadTimeDays = 3 }));
        return (product.GetProperty("id").GetGuid(), warehouse.GetProperty("id").GetGuid(), other.GetProperty("id").GetGuid(), supplier.GetProperty("id").GetGuid());
    }

    protected async Task<JsonElement> Order((Guid Product, Guid Warehouse, Guid OtherWarehouse, Guid Supplier) catalog, int quantity = 10)
        => await Ok(await Send(Client, "/api/orders", new
        {
            supplierId = catalog.Supplier, warehouseId = catalog.Warehouse,
            expectedDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"), notes = "Restock packaging",
            lines = new[] { new { productId = catalog.Product, quantity, unitCost = 2500 } }
        }));

    private sealed class WorkspaceFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Database", connection);
            builder.UseSetting("Security:SecureCookies", "false");
        }
    }
}
