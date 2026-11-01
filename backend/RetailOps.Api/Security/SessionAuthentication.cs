using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetailOps.Api.Data;

namespace RetailOps.Api.Security;

public sealed class SessionAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkspaceSession";
    public const string CookieName = "retailops.session";
    public static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Path.StartsWithSegments("/health")) return AuthenticateResult.NoResult();
        if (!Request.Cookies.TryGetValue(CookieName, out var token) || token.Length is < 32 or > 128)
            return AuthenticateResult.NoResult();
        var hash = Digest(token);
        var session = await db.Sessions.AsNoTracking().Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == hash && x.ExpiresAt > DateTimeOffset.UtcNow, Context.RequestAborted);
        if (session is null || !session.User.IsActive)
            return AuthenticateResult.Fail("Session is no longer valid.");
        var user = session.User;
        Context.Items["Actor"] = user;
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role)
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
