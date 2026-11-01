using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Data;
using Xunit;

namespace RetailOps.Tests;

public sealed class AuthenticationTests : ApiTest
{
    [Fact]
    public async Task SetupIsSingleUseAndPasswordsAreHashed()
    {
        var before = await Client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(before.GetProperty("setupRequired").GetBoolean());
        var user = await Setup();
        Assert.Equal("Admin", user.GetProperty("role").GetString());
        using var anonymous = NewClient();
        var second = await Send(anonymous, "/api/auth/setup", new
        { organizationName = "Second workspace", fullName = "Another admin", email = "second@retailops.test", password = Password });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Users.SingleAsync();
        Assert.NotEqual(Password, stored.PasswordHash);
        Assert.Single(await db.Organizations.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentWorkspaceSetupCreatesOnlyOneAdministrator()
    {
        using var other = NewClient();
        var request = new { organizationName = "Workspace", fullName = "Administrator", email = "admin@retailops.test", password = Password };
        var results = await Task.WhenAll(Send(Client, "/api/auth/setup", request), Send(other, "/api/auth/setup", request));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        using var scope = Factory.Services.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task SetupAndAuthenticatedWritesRequireAntiforgeryTokens()
    {
        var blocked = await Client.PostAsJsonAsync("/api/auth/setup", new
        { organizationName = "Workspace", fullName = "Administrator", email = "admin@retailops.test", password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        await Setup();
        blocked = await Client.PostAsJsonAsync("/api/warehouses", new { code = "UNSAFE", name = "Unverified request" });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var warehouses = await Client.GetFromJsonAsync<JsonElement>("/api/warehouses");
        Assert.Equal(0, warehouses.GetArrayLength());
    }

    [Fact]
    public async Task LogoutRevokesAnExistingSession()
    {
        var setup = await Send(Client, "/api/auth/setup", new
        { organizationName = "Workspace", fullName = "Administrator", email = "admin@retailops.test", password = Password });
        Assert.True(setup.IsSuccessStatusCode);
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("retailops.session=", StringComparison.Ordinal)).Split(';')[0];
        using var copiedSession = Factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false });
        copiedSession.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await copiedSession.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(Client, "/api/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copiedSession.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ViewerCannotMutateAndInactiveUsersImmediatelyLoseAccess()
    {
        await Setup();
        using var viewer = await CreateAccount("Viewer", "viewer@retailops.test");
        var write = await Send(viewer, "/api/warehouses", new { code = "NO", name = "Unauthorized warehouse" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var users = await Client.GetFromJsonAsync<JsonElement>("/api/users");
        var id = users.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("role").GetString() == "Viewer").GetProperty("id").GetGuid();
        await Ok(await Send(Client, $"/api/users/{id}", new { role = "Viewer", isActive = false }, HttpMethod.Patch));
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task WeakPasswordsAndRemovingOwnAdminAccessAreRejected()
    {
        var admin = await Setup();
        var weak = await Send(Client, "/api/users", new
        { fullName = "New Account", email = "weak@retailops.test", password = "short", role = "Viewer" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var id = admin.GetProperty("id").GetGuid();
        var demotion = await Send(Client, $"/api/users/{id}", new { role = "Viewer", isActive = false }, HttpMethod.Patch);
        Assert.Equal(HttpStatusCode.Conflict, demotion.StatusCode);
    }

    [Fact]
    public async Task RepeatedInvalidPasswordsTemporarilyLockTheAccount()
    {
        await Setup();
        using var client = NewClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/api/auth/login", new { email = "admin@retailops.test", password = "WrongPassword324!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "/api/auth/login", new { email = "admin@retailops.test", password = Password })).StatusCode);
    }

    [Fact]
    public async Task UnknownApiEndpointsNeverReturnTheFrontendDocument()
    {
        var response = await Client.GetAsync("/api/no-such-endpoint");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.ToString() ?? "");
    }
}
