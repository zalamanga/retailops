using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RetailOps.Api.Auth;
using RetailOps.Api.Data;
using RetailOps.Api.Endpoints;
using RetailOps.Api.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => { options.UseUtcTimestamp = true; options.TimestampFormat = "O"; });
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Database before starting RetailOps.");
var secureCookies = builder.Configuration.GetValue<bool?>("Security:SecureCookies") ?? !builder.Environment.IsDevelopment();
builder.Configuration["Security:SecureCookies"] = secureCookies.ToString();
if (!builder.Environment.IsDevelopment() && !secureCookies)
    throw new InvalidOperationException("Production requires Secure cookies and HTTPS termination.");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<PasswordSecurity>();
var protection = builder.Services.AddDataProtection().SetApplicationName("RetailOps");
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(keyPath))
{
    Directory.CreateDirectory(keyPath);
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}
builder.Services.AddAuthentication(SessionAuthentication.SchemeName).AddScheme<AuthenticationSchemeOptions, SessionAuthentication>(SessionAuthentication.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireRole(RoleNames.Admin));
    options.AddPolicy("Catalog", policy => policy.RequireRole(RoleNames.Admin, RoleNames.Manager));
    options.AddPolicy("Approve", policy => policy.RequireRole(RoleNames.Admin, RoleNames.Manager));
    options.AddPolicy("Operate", policy => policy.RequireRole(RoleNames.Admin, RoleNames.Manager, RoleNames.Operator));
    options.AddPolicy("Backoffice", policy => policy.RequireRole(RoleNames.Admin, RoleNames.Manager, RoleNames.Operator, RoleNames.Viewer));
    options.AddPolicy("Retail", policy => policy.RequireRole(RoleNames.Admin, RoleNames.Manager, RoleNames.Cashier));
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "retailops.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("Security:TrustedProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddOpenApi();
var app = builder.Build();

if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    return;
}

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var timer = Stopwatch.StartNew();
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
        context.Response.Headers["X-Request-ID"] = context.TraceIdentifier;
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    try { await next(context); }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var (status, title, detail, errors) = exception switch
        {
            AppException error => (error.Status, error.Title, error.Message, error.Errors),
            AntiforgeryValidationException => (400, "Request verification failed", "Refresh the page and try again.", null),
            BadHttpRequestException => (400, "Invalid request", "Check the request body and try again.", null),
            JsonException => (400, "Invalid JSON", "Check the request body and try again.", null),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (409, "Record already exists", "A record with these unique details already exists. Refresh and try again.", null),
            _ => (500, "Unexpected server error", "The request could not be completed. Try again or share the request ID with your administrator.", null)
        };
        if (status >= 500) app.Logger.LogError(exception, "Request {RequestId} failed", context.TraceIdentifier);
        context.Response.Clear();
        context.Response.StatusCode = status;
        await Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?>
        { ["traceId"] = context.TraceIdentifier, ["errors"] = errors }).ExecuteAsync(context);
    }
    finally
    {
        if (context.Request.Path.StartsWithSegments("/api"))
            app.Logger.LogInformation("HTTP {Method} {Path} returned {StatusCode} in {ElapsedMs}ms; request {RequestId}",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode, timer.ElapsedMilliseconds, context.TraceIdentifier);
    }
});
app.UseStatusCodePages(async statusContext =>
{
    var context = statusContext.HttpContext;
    if (!context.Request.Path.StartsWithSegments("/api")) return;
    var status = context.Response.StatusCode;
    var (title, detail) = status switch
    {
        400 => ("Invalid request", "Check the request body and try again."),
        401 => ("Sign-in required", "Please sign in to continue."),
        403 => ("Permission required", "Your account does not have permission for this operation."),
        404 => ("Endpoint not found", "The requested API endpoint does not exist."),
        405 => ("Method not allowed", "Use the HTTP method documented for this endpoint."),
        429 => ("Too many attempts", "Wait a minute before trying again."),
        _ => ("Request could not be completed", "Check the request and try again.")
    };
    await Results.Problem(statusCode: status, title: title, detail: detail,
        extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps && !context.Request.Path.StartsWithSegments("/health"))
        {
            await Results.Problem(statusCode: 426, title: "HTTPS required", detail: "Access RetailOps through its HTTPS reverse proxy.",
                extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
            return;
        }
        await next(context);
    });
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
         HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)))
    {
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        if (context.Request.ContentLength > 0 && !context.Request.HasJsonContentType())
            throw new AppException(415, "JSON required", "Send the request body as application/json.");
    }
    await next(context);
});
app.MapAuthEndpoints();
app.MapBusinessEndpoints();
app.MapRetailEndpoints();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.Map("/api/{**path}", (HttpContext context) => Results.Problem(statusCode: 404, title: "Endpoint not found",
    detail: "The requested API endpoint does not exist.",
    extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
