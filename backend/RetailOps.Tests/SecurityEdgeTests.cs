using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailOps.Api.Data;
using Xunit;

namespace RetailOps.Tests;

public sealed class SecurityEdgeTests : ApiTest
{
    [Fact]
    public async Task Unauthorized_forbidden_invalid_json_and_unknown_api_errors_have_problem_details_and_security_headers()
    {
        await AssertApiProblem(await Client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        await AssertApiProblem(await Client.GetAsync("/api/not-an-endpoint"), HttpStatusCode.NotFound);
        await Setup();
        using var viewer = await CreateAccount("Viewer", "viewer@retailops.test");
        await AssertApiProblem(await viewer.GetAsync("/api/users"), HttpStatusCode.Forbidden);

        var token = await Client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/products");
        malformed.Headers.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        malformed.Content = new StringContent("{\"sku\": ", Encoding.UTF8, "application/json");
        await AssertApiProblem(await Client.SendAsync(malformed), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Very_large_user_page_returns_validation_error_instead_of_integer_overflow()
    {
        await Setup();
        var response = await Client.GetAsync("/api/users?page=2147483647&pageSize=100");
        await AssertApiProblem(response, HttpStatusCode.BadRequest);
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reciprocal_admin_changes_allow_one_writer_and_preserve_an_active_administrator(bool deactivate)
    {
        var first = await Setup();
        using var second = await CreateAccount("Admin", "second-admin@retailops.test");
        var secondUser = await second.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var firstId = first.GetProperty("id").GetGuid();
        var secondId = secondUser.GetProperty("id").GetGuid();
        var change = new { role = deactivate ? "Admin" : "Viewer", isActive = !deactivate };
        var responses = await Task.WhenAll(
            Send(Client, $"/api/users/{secondId}", change, HttpMethod.Patch),
            Send(second, $"/api/users/{firstId}", change, HttpMethod.Patch)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(responses, x => x.IsSuccessStatusCode);
        var denied = Assert.Single(responses, x => !x.IsSuccessStatusCode);
        Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(x => x.IsActive && x.Role == "Admin"));
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal(1, deactivate
            ? await db.Users.CountAsync(x => !x.IsActive)
            : await db.Users.CountAsync(x => x.Role == "Viewer"));
    }

    [Fact]
    public async Task Expired_server_side_session_cannot_authenticate_even_with_its_original_cookie()
    {
        await Setup();
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/auth/me")).StatusCode);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.SingleAsync();
            session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await AssertApiProblem(await Client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        await AssertApiProblem(await Client.GetAsync("/api/inventory"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_and_logout_reject_missing_or_invalid_antiforgery_tokens_without_changing_authentication()
    {
        await Setup();
        using var anonymous = NewClient();
        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = "admin@retailops.test", password = Password });
        await AssertApiProblem(login, HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);

        await AssertApiProblem(await Client.PostAsync("/api/auth/logout", null), HttpStatusCode.BadRequest);
        using var invalidToken = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        invalidToken.Headers.Add("X-CSRF-TOKEN", "not-a-valid-antiforgery-token");
        await AssertApiProblem(await Client.SendAsync(invalidToken), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(Client, "/api/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Operator_can_prepare_and_receive_orders_and_move_stock_but_cannot_maintain_catalog_or_approve()
    {
        await Setup();
        var catalog = await Catalog();
        using var warehouseOperator = await CreateAccount("Operator", "operator@retailops.test");
        using var manager = await CreateAccount("Manager", "manager@retailops.test");
        await AssertApiProblem(await Send(warehouseOperator, "/api/products", new
        { sku = "UNAUTHORIZED", name = "Blocked product", unit = "unit", reorderPoint = 0, standardCost = 1, isActive = true }), HttpStatusCode.Forbidden);
        await AssertApiProblem(await Send(warehouseOperator, "/api/warehouses", new { code = "NO", name = "Blocked warehouse" }), HttpStatusCode.Forbidden);
        await AssertApiProblem(await Send(warehouseOperator, "/api/suppliers", new
        { name = "Blocked supplier", email = "supplier@retailops.test", phone = "", leadTimeDays = 1 }), HttpStatusCode.Forbidden);
        await AssertApiProblem(await warehouseOperator.GetAsync("/api/users"), HttpStatusCode.Forbidden);
        await AssertApiProblem(await manager.GetAsync("/api/users"), HttpStatusCode.Forbidden);

        var order = await Ok(await Send(warehouseOperator, "/api/orders", new
        {
            supplierId = catalog.Supplier, warehouseId = catalog.Warehouse,
            expectedDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"), notes = "Warehouse replenishment",
            lines = new[] { new { productId = catalog.Product, quantity = 10, unitCost = 2500 } }
        }));
        var id = order.GetProperty("id").GetGuid();
        await Ok(await Send(warehouseOperator, $"/api/orders/{id}/submit"));
        await AssertApiProblem(await Send(warehouseOperator, $"/api/orders/{id}/approve"), HttpStatusCode.Forbidden);
        await Ok(await Send(manager, $"/api/orders/{id}/approve"));
        var receipt = await Ok(await Send(warehouseOperator, $"/api/orders/{id}/receipts", new
        { notes = "First delivery", lines = new[] { new { orderLineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 2 } } },
            idempotencyKey: Guid.NewGuid().ToString()));
        Assert.Equal("PartiallyReceived", receipt.GetProperty("order").GetProperty("status").GetString());
        await Ok(await Send(warehouseOperator, "/api/inventory/adjustments", new
        { productId = catalog.Product, warehouseId = catalog.Warehouse, quantity = 3, reason = "Counted unopened cartons" }));
        await Ok(await Send(warehouseOperator, "/api/inventory/transfers", new
        { productId = catalog.Product, fromWarehouseId = catalog.Warehouse, toWarehouseId = catalog.OtherWarehouse, quantity = 1, reason = "East warehouse allocation" }));
        var inventory = await warehouseOperator.GetFromJsonAsync<JsonElement>("/api/inventory");
        Assert.Equal(4, inventory.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("warehouseId").GetGuid() == catalog.Warehouse).GetProperty("onHand").GetInt64());
        Assert.Equal(1, inventory.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("warehouseId").GetGuid() == catalog.OtherWarehouse).GetProperty("onHand").GetInt64());
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockMovements.CountAsync());
    }

    [Fact]
    public async Task Twenty_authentication_attempts_consume_the_window_and_the_next_has_a_429_problem()
    {
        var invalidSetup = new { organizationName = "Security verification", fullName = "First Administrator",
            email = "admin@retailops.test", password = "short" };
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(Client, "/api/auth/setup", invalidSetup)).StatusCode);
        await AssertApiProblem(await Send(Client, "/api/auth/setup", invalidSetup), HttpStatusCode.TooManyRequests);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Organizations.CountAsync());
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public void Production_refuses_to_start_when_secure_cookies_are_disabled()
    {
        using var production = new ProductionFactory(DatabaseConnection, false);
        var exception = Record.Exception(() =>
        {
            using var client = production.CreateClient();
        });
        Assert.NotNull(exception);
        Assert.Contains("Production requires Secure cookies", exception.ToString());
    }

    [Fact]
    public async Task Production_requires_https_for_the_workspace_and_keeps_health_checks_available()
    {
        await using var production = new ProductionFactory(DatabaseConnection, true);
        using var insecureClient = production.CreateClient(new() { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        await AssertApiProblem(await insecureClient.GetAsync("/api/auth/status"), HttpStatusCode.UpgradeRequired);
        Assert.Equal(HttpStatusCode.OK, (await insecureClient.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await insecureClient.GetAsync("/health/ready")).StatusCode);

        using var secureClient = production.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var status = await secureClient.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(status.GetProperty("setupRequired").GetBoolean());
        var setup = await Send(secureClient, "/api/auth/setup", new
        { organizationName = "Secure workspace", fullName = "Administrator", email = "secure@retailops.test", password = Password });
        await Ok(setup);
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("retailops.session=", StringComparison.Ordinal));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await secureClient.GetAsync("/api/auth/me")).StatusCode);
    }

    private static async Task AssertApiProblem(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        var problem = json.RootElement;
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        var trace = problem.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(trace));
        Assert.Equal(trace, Assert.Single(response.Headers.GetValues("X-Request-ID")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore == true, body);
    }

    private sealed class ProductionFactory(string connection, bool secureCookies) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", connection);
            builder.UseSetting("Security:SecureCookies", secureCookies.ToString());
        }
    }
}
