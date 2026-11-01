using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using RetailOps.Api.Data;
using RetailOps.Api.Domain;
using RetailOps.Api.Security;

namespace RetailOps.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Authentication");
        auth.MapGet("/status", async (AppDbContext db, CancellationToken ct) => new { setupRequired = !await db.Organizations.AnyAsync(ct) });
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) => new { token = antiforgery.GetAndStoreTokens(context).RequestToken! });
        auth.MapPost("/setup", Setup).RequireRateLimiting("auth");
        auth.MapPost("/login", Login).RequireRateLimiting("auth");
        auth.MapGet("/me", async (HttpContext context, AppDbContext db, CancellationToken ct) => await UserView(context.Actor(), db, ct)).RequireAuthorization();
        auth.MapPost("/logout", Logout).RequireAuthorization();

        var users = app.MapGroup("/api/users").RequireAuthorization("Admin").WithTags("Users");
        users.MapGet("", async (AppDbContext db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var number = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            if ((long)(number - 1) * size > int.MaxValue)
                throw new AppException(400, "Invalid request", "Choose a smaller page number.", new() { ["page"] = ["The requested page is outside the supported range."] });
            var query = db.Users.AsNoTracking().OrderBy(x => x.FullName);
            var total = await query.CountAsync(ct);
            var items = await query.Skip((number - 1) * size).Take(size)
                .Select(x => new { x.Id, x.FullName, x.Email, x.Role, x.IsActive }).ToListAsync(ct);
            return Results.Ok(new { items, total, page = number, pageSize = size });
        });
        users.MapPost("", CreateUser);
        users.MapPatch("/{id:guid}", UpdateUser);
    }

    private static async Task<IResult> Setup(SetupRequest request, HttpContext context, AppDbContext db,
        PasswordSecurity passwords, IConfiguration config, CancellationToken ct)
    {
        var organizationName = Text(request.OrganizationName, "organizationName", 2, 100);
        var name = Text(request.FullName, "fullName", 2, 100);
        var email = Email(request.Email);
        PasswordSecurity.Validate(request.Password ?? "");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(578190021)", ct);
        if (await db.Organizations.AnyAsync(ct))
            throw new AppException(409, "Workspace already configured", "Sign in or contact your workspace administrator.");
        var user = new User { FullName = name, Email = email, Role = RoleNames.Admin };
        user.PasswordHash = passwords.Hash(user, request.Password!);
        db.Organizations.Add(new Organization { Name = organizationName });
        db.Users.Add(user);
        Audit(db, user, "workspace.created", "Organization", "workspace", "Workspace configured.");
        AddSession(db, user, context, config);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(await UserView(user, db, ct));
    }

    private static async Task<IResult> Login(LoginRequest request, HttpContext context, AppDbContext db,
        PasswordSecurity passwords, IConfiguration config, CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length > 254 || (request.Password?.Length ?? 0) > 128)
            throw new AppException(401, "Sign-in failed", "Email or password is incorrect, or the account is temporarily locked.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"auth:" + email}, 0))", ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email, ct);
        var validPassword = passwords.Verify(user, request.Password ?? "");
        var locked = user?.LockoutUntil > DateTimeOffset.UtcNow;
        if (user is null || !user.IsActive || !validPassword || locked)
        {
            if (user is not null && !locked)
            {
                user.FailedLogins++;
                if (user.FailedLogins >= 5)
                {
                    user.LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(15);
                    user.FailedLogins = 0;
                }
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            throw new AppException(401, "Sign-in failed", "Email or password is incorrect, or the account is temporarily locked.");
        }
        user.FailedLogins = 0;
        user.LockoutUntil = null;
        await db.Sessions.Where(x => x.ExpiresAt <= DateTimeOffset.UtcNow).ExecuteDeleteAsync(ct);
        AddSession(db, user, context, config);
        Audit(db, user, "auth.signed_in", "User", user.Id.ToString(), "Signed in.");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(await UserView(user, db, ct));
    }

    private static async Task<IResult> Logout(HttpContext context, AppDbContext db, CancellationToken ct)
    {
        var actor = context.Actor();
        var token = context.Request.Cookies[SessionAuthentication.CookieName] ?? "";
        var hash = SessionAuthentication.Digest(token);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Sessions.Where(x => x.TokenHash == hash).ExecuteDeleteAsync(ct);
        Audit(db, actor, "auth.signed_out", "User", actor.Id.ToString(), "Signed out.");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        context.Response.Cookies.Delete(SessionAuthentication.CookieName, new CookieOptions { Path = "/" });
        return Results.NoContent();
    }

    private static async Task<IResult> CreateUser(CreateUserRequest request, HttpContext context, AppDbContext db,
        PasswordSecurity passwords, CancellationToken ct)
    {
        var name = Text(request.FullName, "fullName", 2, 100);
        var email = Email(request.Email);
        var role = Role(request.Role);
        PasswordSecurity.Validate(request.Password ?? "");
        var user = new User { FullName = name, Email = email, Role = role };
        user.PasswordHash = passwords.Hash(user, request.Password!);
        var actor = context.Actor();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(578190022)", ct);
        if (!await db.Users.AnyAsync(x => x.Id == actor.Id && x.IsActive && x.Role == RoleNames.Admin, ct))
            throw new AppException(403, "Administrator access required", "Your access has changed. Refresh the page to continue.");
        if (await db.Users.AnyAsync(x => x.Email == email, ct))
            throw new AppException(409, "Email already registered", "Use a different email address.");
        db.Users.Add(user);
        Audit(db, actor, "user.created", "User", user.Id.ToString(), $"Created account for {name} ({role}).");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/users/{user.Id}", new { user.Id, user.FullName, user.Email, user.Role, user.IsActive });
    }

    private static async Task<IResult> UpdateUser(Guid id, UpdateUserRequest request, HttpContext context, AppDbContext db, CancellationToken ct)
    {
        var actor = context.Actor();
        var role = Role(request.Role);
        if (id == actor.Id && (!request.IsActive || role != RoleNames.Admin))
            throw new AppException(409, "Keep administrator access", "You cannot deactivate your own account or remove your own administrator role.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(578190022)", ct);
        await Services.Accounting.LockUsers(db, [actor.Id, id], ct);
        if (!await db.Users.AnyAsync(x => x.Id == actor.Id && x.IsActive && x.Role == RoleNames.Admin, ct))
            throw new AppException(403, "Administrator access required", "Your access has changed. Refresh the page to continue.");
        var user = await db.Users.FindAsync([id], ct) ?? throw new AppException(404, "Account not found", "The account no longer exists.");
        user.Role = role;
        user.IsActive = request.IsActive;
        if (!user.IsActive) await db.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
        Audit(db, actor, "user.updated", "User", id.ToString(), $"Updated account {user.FullName}: role {role}, active {request.IsActive}.");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { user.Id, user.FullName, user.Email, user.Role, user.IsActive });
    }

    private static void AddSession(AppDbContext db, User user, HttpContext context, IConfiguration config)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddHours(8);
        db.Sessions.Add(new UserSession { UserId = user.Id, TokenHash = SessionAuthentication.Digest(token), ExpiresAt = expires });
        context.Response.Cookies.Append(SessionAuthentication.CookieName, token, new CookieOptions
        {
            HttpOnly = true, Secure = config.GetValue<bool>("Security:SecureCookies"), SameSite = SameSiteMode.Strict,
            Path = "/", Expires = expires, IsEssential = true
        });
    }

    private static async Task<object> UserView(User user, AppDbContext db, CancellationToken ct) => new
    {
        user.Id, user.FullName, user.Email, user.Role,
        organizationName = await db.Organizations.Select(x => x.Name).SingleAsync(ct),
        organizationId = await db.Organizations.Select(x => x.Id).SingleAsync(ct)
    };

    private static void Audit(AppDbContext db, User actor, string action, string type, string id, string description) =>
        db.AuditEvents.Add(new AuditEvent
        {
            ActorId = actor.Id, ActorName = actor.FullName, Action = action,
            EntityType = type, EntityId = id, Description = description
        });

    private static string Text(string? text, string field, int min, int max)
    {
        var value = (text ?? "").Trim();
        if (value.Length < min || value.Length > max)
            throw new AppException(400, "Invalid request", "Check the highlighted fields.", new() { [field] = [$"Use {min}-{max} characters."] });
        return value;
    }

    private static string Email(string? email)
    {
        var value = Text(email, "email", 3, 254).ToLowerInvariant();
        if (!MailAddress.TryCreate(value, out var address) || address.Address != value)
            throw new AppException(400, "Invalid request", "Enter a valid email address.", new() { ["email"] = ["Enter a valid email address."] });
        return value;
    }

    private static string Role(string? role) => RoleNames.All.Contains(role ?? "") ? role!
        : throw new AppException(400, "Invalid request", "Choose a valid account role.", new() { ["role"] = ["Choose Admin, Manager, Operator, Viewer, or Cashier."] });

    private sealed record SetupRequest(string? OrganizationName, string? FullName, string? Email, string? Password);
    private sealed record LoginRequest(string? Email, string? Password);
    private sealed record CreateUserRequest(string? FullName, string? Email, string? Password, string? Role);
    private sealed record UpdateUserRequest(string? Role, bool IsActive);
}
