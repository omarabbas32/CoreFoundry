using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Users;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class UserTests
{
    private const string Hash = "AQAAAAIAAYagAAAAE...";

    [Theory]
    [InlineData("Omar@Example.com", "omar@example.com")]
    [InlineData("  dev@corefoundry.dev  ", "dev@corefoundry.dev")]
    public void Email_is_trimmed_and_lower_cased(string input, string expected) =>
        new User(input, Hash).Email.ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("a@b@c")]
    public void Invalid_email_is_rejected(string email) =>
        Should.Throw<DomainException>(() => new User(email, Hash));

    [Fact]
    public void Email_longer_than_254_characters_is_rejected() =>
        Should.Throw<DomainException>(() => new User(new string('a', 250) + "@x.io", Hash));

    [Fact]
    public void Password_hash_is_required() =>
        Should.Throw<DomainException>(() => new User("a@b.io", " "));
}
