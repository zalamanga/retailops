using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RetailOps.Tests;

public sealed class RetailQueuedIdentityTests : RetailApiTest
{
    [Fact]
    public async Task Changed_cookie_owner_cannot_execute_or_replay_a_queued_shift_and_does_not_reserve_its_key()
    {
        using var f = await Retail();
        await Assign(f.OtherCashierId, f.Branch);
        var organization = (await Get(f.OtherCashier, "/api/auth/me")).GetProperty("organizationId").GetGuid().ToString();
        var key = Key();
        var body = new { branchId = f.Branch, openingCash = 250m };

        await IdentityDenied(await Queued(f.OtherCashier, "/api/retail/shifts", body, key,
            [f.CashierId.ToString()], [organization]));
        Assert.Empty((await Get(f.OtherCashier, "/api/retail/shifts")).EnumerateArray());

        var accepted = await Ok(await Send(f.OtherCashier, "/api/retail/shifts", body, idempotencyKey: key));
        var shiftId = accepted.GetProperty("id").GetGuid();
        await IdentityDenied(await Queued(f.OtherCashier, "/api/retail/shifts", body, key,
            [f.CashierId.ToString()], [organization]));
        var replay = await Ok(await Queued(f.OtherCashier, "/api/retail/shifts", body, key,
            [f.OtherCashierId.ToString()], [organization]));
        Assert.Equal(shiftId, replay.GetProperty("id").GetGuid());
        Assert.Equal("Open", replay.GetProperty("status").GetString());
        Assert.Single((await Get(f.OtherCashier, "/api/retail/shifts")).EnumerateArray());
        Assert.Equal(f.Shift, Assert.Single((await Get(f.Cashier, "/api/retail/shifts")).EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Partial_malformed_duplicate_and_wrong_workspace_headers_are_rejected_before_mutation_effects()
    {
        using var f = await Retail();
        var actor = f.OtherCashierId.ToString();
        var organization = (await Get(f.OtherCashier, "/api/auth/me")).GetProperty("organizationId").GetGuid().ToString();
        var key = Key();
        var body = new { branchId = f.OtherBranch, openingCash = 0m };
        (string[]? Actors, string[]? Organizations)[] invalid =
        [
            ([actor], null),
            (null, [organization]),
            ([""], [organization]),
            (["not-a-guid"], [organization]),
            ([Guid.Empty.ToString()], [organization]),
            ([actor], [Guid.Empty.ToString()]),
            ([actor], [Guid.NewGuid().ToString()]),
            ([actor, actor], [organization]),
            ([actor], [organization, organization])
        ];
        foreach (var headers in invalid)
        {
            await IdentityDenied(await Queued(f.OtherCashier, "/api/retail/shifts", body, key,
                headers.Actors, headers.Organizations));
            Assert.Empty((await Get(f.OtherCashier, "/api/retail/shifts")).EnumerateArray());
        }
        var accepted = await Ok(await Send(f.OtherCashier, "/api/retail/shifts", body, idempotencyKey: key));
        Assert.Equal(accepted.GetProperty("id").GetGuid(),
            Assert.Single((await Get(f.OtherCashier, "/api/retail/shifts")).EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Matching_queued_identity_allows_sale_and_replay_while_read_requests_ignore_mutation_headers()
    {
        using var f = await Retail();
        var actor = f.CashierId.ToString();
        var organization = (await Get(f.Cashier, "/api/auth/me")).GetProperty("organizationId").GetGuid().ToString();
        var key = Key();
        var command = Command(f);
        await IdentityDenied(await Queued(f.Cashier, "/api/retail/sales", command, key,
            [actor], [Guid.NewGuid().ToString()]));
        Assert.Equal(20, await Stock(f.Product, f.Warehouse));
        Assert.Empty((await Get(f.Cashier, "/api/retail/sales")).EnumerateArray());

        var accepted = await Ok(await Queued(f.Cashier, "/api/retail/sales", command, key,
            [actor], [organization]));
        var replay = await Ok(await Queued(f.Cashier, "/api/retail/sales", command, key,
            [actor], [organization]));
        Assert.Equal(accepted.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(19, await Stock(f.Product, f.Warehouse));

        using var read = new HttpRequestMessage(HttpMethod.Get, $"/api/retail/catalog?branchId={f.Branch}");
        read.Headers.Add("X-Retail-Actor", "not-a-guid");
        var catalog = await Ok(await f.Cashier.SendAsync(read));
        Assert.Equal(19, Assert.Single(catalog.GetProperty("products").EnumerateArray()).GetProperty("onHand").GetInt64());
    }

    private static async Task<HttpResponseMessage> Queued(HttpClient client, string path, object body, string key,
        string[]? actors, string[]? organizations)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Headers.Add("Idempotency-Key", key);
        if (actors is not null) request.Headers.TryAddWithoutValidation("X-Retail-Actor", actors);
        if (organizations is not null) request.Headers.TryAddWithoutValidation("X-Retail-Organization", organizations);
        return await client.SendAsync(request);
    }

    private static async Task IdentityDenied(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Queued identity changed", problem.GetProperty("title").GetString());
        }
    }
}
