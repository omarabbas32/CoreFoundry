using CoreFoundry.Application.Common;
using Shouldly;

namespace CoreFoundry.UnitTests.Auth;

public class AuthServiceTests
{
    private const string Email = "omar@example.com";
    private const string Password = "correct horse battery";

    private readonly AuthHarness _auth = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- register ----------

    [Fact]
    public async Task Register_creates_the_user_and_stores_only_the_refresh_token_hash()
    {
        var result = await _auth.Service.RegisterAsync("  Omar@Example.com ", Password, Ct);

        result.User.Email.ShouldBe(Email);
        result.User.Id.ShouldBeGreaterThan(0);
        var stored = _auth.StoredToken(result.RefreshToken.RawValue);
        stored.TokenHash.ShouldNotBe(result.RefreshToken.RawValue);
        stored.UserId.ShouldBe(result.User.Id);
    }

    [Fact]
    public async Task Register_rejects_an_email_that_is_taken_ignoring_case()
    {
        await _auth.Service.RegisterAsync(Email, Password, Ct);

        await Should.ThrowAsync<ConflictException>(() => _auth.Service.RegisterAsync("OMAR@example.com", Password, Ct));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("123456789")]
    public async Task Register_rejects_passwords_under_10_characters(string password)
    {
        var error = await Should.ThrowAsync<ValidationFailedException>(() => _auth.Service.RegisterAsync(Email, password, Ct));

        error.Errors.Keys.ShouldBe(["password"]);
    }

    [Fact]
    public async Task Register_rejects_an_invalid_email()
    {
        var error = await Should.ThrowAsync<ValidationFailedException>(() => _auth.Service.RegisterAsync("not-an-email", Password, Ct));

        error.Errors.Keys.ShouldBe(["email"]);
    }

    // ---------- login ----------

    [Fact]
    public async Task Login_with_correct_credentials_issues_tokens()
    {
        await _auth.Service.RegisterAsync(Email, Password, Ct);

        var result = await _auth.Service.LoginAsync("Omar@Example.com", Password, Ct);

        result.AccessToken.Value.ShouldNotBeNullOrEmpty();
        _auth.StoredToken(result.RefreshToken.RawValue).IsActive(_auth.Clock.Now.UtcDateTime).ShouldBeTrue();
    }

    [Fact]
    public async Task Login_with_wrong_password_fails()
    {
        await _auth.Service.RegisterAsync(Email, Password, Ct);

        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.LoginAsync(Email, "wrong password!", Ct));
    }

    [Fact]
    public async Task Login_with_unknown_email_fails_the_same_way_and_still_spends_a_hash()
    {
        var hashesBefore = _auth.Hasher.HashCalls;

        var error = await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.LoginAsync("nobody@example.com", Password, Ct));

        error.Message.ShouldBe(new AuthenticationFailedException().Message);
        _auth.Hasher.HashCalls.ShouldBe(hashesBefore + 1);
    }

    [Fact]
    public async Task Login_rehashes_a_password_stored_with_outdated_parameters()
    {
        await _auth.Service.RegisterAsync(Email, Password, Ct);
        _auth.Hasher.ReportRehashNeeded = true;
        var hashesBefore = _auth.Hasher.HashCalls;

        await _auth.Service.LoginAsync(Email, Password, Ct);

        _auth.Hasher.HashCalls.ShouldBe(hashesBefore + 1);
    }

    // ---------- refresh ----------

    [Fact]
    public async Task Refresh_rotates_the_token()
    {
        var login = await _auth.Service.RegisterAsync(Email, Password, Ct);

        var refreshed = await _auth.Service.RefreshAsync(login.RefreshToken.RawValue, Ct);

        var old = _auth.StoredToken(login.RefreshToken.RawValue);
        var replacement = _auth.StoredToken(refreshed.RefreshToken.RawValue);
        old.IsRevoked.ShouldBeTrue();
        old.ReplacedByToken.ShouldBeSameAs(replacement);
        replacement.IsActive(_auth.Clock.Now.UtcDateTime).ShouldBeTrue();
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_the_whole_newer_chain()
    {
        var first = await _auth.Service.RegisterAsync(Email, Password, Ct);
        var second = await _auth.Service.RefreshAsync(first.RefreshToken.RawValue, Ct);
        var third = await _auth.Service.RefreshAsync(second.RefreshToken.RawValue, Ct);

        // An attacker replays the first token.
        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.RefreshAsync(first.RefreshToken.RawValue, Ct));

        _auth.StoredToken(third.RefreshToken.RawValue).IsRevoked.ShouldBeTrue();
        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.RefreshAsync(third.RefreshToken.RawValue, Ct));
    }

    [Fact]
    public async Task Refresh_with_an_expired_token_fails()
    {
        var login = await _auth.Service.RegisterAsync(Email, Password, Ct);
        _auth.Clock.Now = _auth.Clock.Now.AddDays(8);

        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.RefreshAsync(login.RefreshToken.RawValue, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-issued")]
    public async Task Refresh_with_a_missing_or_unknown_token_fails(string? raw) =>
        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.RefreshAsync(raw, Ct));

    [Fact]
    public async Task Losing_a_concurrent_refresh_race_fails_without_revoking_the_chain()
    {
        var login = await _auth.Service.RegisterAsync(Email, Password, Ct);
        _auth.UnitOfWork.FailNextSaveWithConcurrencyConflict = true;

        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.RefreshAsync(login.RefreshToken.RawValue, Ct));
    }

    // ---------- logout / me ----------

    [Fact]
    public async Task Logout_revokes_the_token()
    {
        var login = await _auth.Service.RegisterAsync(Email, Password, Ct);

        await _auth.Service.LogoutAsync(login.RefreshToken.RawValue, Ct);

        _auth.StoredToken(login.RefreshToken.RawValue).IsRevoked.ShouldBeTrue();
        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.RefreshAsync(login.RefreshToken.RawValue, Ct));
    }

    [Fact]
    public async Task Logout_without_a_valid_token_is_a_no_op()
    {
        await _auth.Service.LogoutAsync(null, Ct);
        await _auth.Service.LogoutAsync("never-issued", Ct);

        _auth.UnitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Current_user_is_returned_by_id()
    {
        var login = await _auth.Service.RegisterAsync(Email, Password, Ct);

        (await _auth.Service.GetCurrentUserAsync(login.User.Id, Ct)).Email.ShouldBe(Email);
        await Should.ThrowAsync<AuthenticationFailedException>(() => _auth.Service.GetCurrentUserAsync(999, Ct));
    }
}
