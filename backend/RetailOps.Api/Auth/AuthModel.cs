using Microsoft.EntityFrameworkCore;

namespace RetailOps.Api.Auth;

public static class AuthModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(254);
            entity.Property(x => x.FullName).HasMaxLength(100);
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
            entity.Property(x => x.Role).HasMaxLength(20);
        });
        model.Entity<UserSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Organization>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100);
        });
    }
}
