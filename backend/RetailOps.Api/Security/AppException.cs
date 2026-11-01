using RetailOps.Api.Auth;

namespace RetailOps.Api.Security;

public sealed class AppException(int status, string title, string detail, Dictionary<string, string[]>? errors = null)
    : Exception(detail)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public Dictionary<string, string[]>? Errors { get; } = errors;
}

public static class CurrentUser
{
    public static User Actor(this HttpContext context) => context.Items["Actor"] as User
        ?? throw new AppException(401, "Sign-in required", "Please sign in to continue.");
}
