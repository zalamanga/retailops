namespace RetailOps.Api.Auth;

public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public const string Cashier = "Cashier";
    public static readonly string[] All = [Admin, Manager, Operator, Viewer, Cashier];
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = RoleNames.Viewer;
    public bool IsActive { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
