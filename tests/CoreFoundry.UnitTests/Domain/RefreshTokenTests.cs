using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Users;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class RefreshTokenTests
{
    private static readonly string ValidHash = new('a', RefreshToken.TokenHashLength);
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // upper-case
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // not hex
    public void Token_hash_must_be_lower_case_sha256_hex(string hash) =>
        Should.Throw<DomainException>(() => new RefreshToken(1, hash, Now.AddDays(7)));

    [Fact]
    public void Expiry_must_be_utc() =>
        Should.Throw<DomainException>(() => new RefreshToken(1, ValidHash, DateTime.Now.AddDays(7)));

    [Fact]
    public void Token_is_active_until_it_expires()
    {
        var token = new RefreshToken(1, ValidHash, Now.AddDays(7));

        token.IsActive(Now).ShouldBeTrue();
        token.IsActive(Now.AddDays(7)).ShouldBeFalse();
    }

    [Fact]
    public void Rotation_revokes_and_links_the_replacement()
    {
        var old = new RefreshToken(1, ValidHash, Now.AddDays(7));
        var replacement = new RefreshToken(1, new string('b', 64), Now.AddDays(7));

        old.Revoke(Now, replacement);

        old.IsActive(Now).ShouldBeFalse();
        old.RevokedAt.ShouldBe(Now);
        old.ReplacedByToken.ShouldBeSameAs(replacement);
    }

    [Fact]
    public void Revoking_twice_is_rejected()
    {
        var token = new RefreshToken(1, ValidHash, Now.AddDays(7));
        token.Revoke(Now);

        Should.Throw<DomainException>(() => token.Revoke(Now));
    }
}
