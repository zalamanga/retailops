using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RetailOps.Api.Auth;

namespace RetailOps.Api.Security;

public sealed class PasswordSecurity
{
    private readonly PasswordHasher<User> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    private readonly User dummyUser = new();
    private readonly string dummyHash;

    public PasswordSecurity() => dummyHash = hasher.HashPassword(dummyUser, Guid.NewGuid() + "Aa1!");
    public string Hash(User user, string password) => hasher.HashPassword(user, password);
    public bool Verify(User? user, string password)
    {
        var result = hasher.VerifyHashedPassword(user ?? dummyUser, user?.PasswordHash ?? dummyHash, password);
        return user is not null && result != PasswordVerificationResult.Failed;
    }

    public static void Validate(string password)
    {
        if (password.Length is < 12 or > 128 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            throw new AppException(400, "Invalid request", "Choose a stronger password.", new()
            {
                ["password"] = ["Use 12-128 characters with an uppercase letter, lowercase letter, and number."]
            });
    }
}
