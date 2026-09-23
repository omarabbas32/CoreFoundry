using CoreFoundry.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreFoundry.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.HasIndex(user => user.Email).IsUnique();

        builder.Property(user => user.PasswordHash).HasMaxLength(User.PasswordHashMaxLength).IsRequired();
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(token => token.Id);

        builder.Property(token => token.TokenHash)
            .HasColumnType($"char({RefreshToken.TokenHashLength})")
            .IsRequired();
        builder.HasIndex(token => token.TokenHash).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Rotation chain; cleaning up old tokens must not fail on a link.
        builder.HasOne(token => token.ReplacedByToken)
            .WithMany()
            .HasForeignKey(token => token.ReplacedByTokenId)
            .OnDelete(DeleteBehavior.SetNull);

        // Revoking is a compare-and-set: UPDATE ... WHERE RevokedAt IS NULL. If two requests rotate the
        // same token at once, the second save fails instead of minting a second valid successor.
        builder.Property(token => token.RevokedAt).IsConcurrencyToken();

        builder.Ignore(token => token.IsRevoked);
    }
}
