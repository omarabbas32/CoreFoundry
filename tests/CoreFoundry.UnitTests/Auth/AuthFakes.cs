using System.Security.Cryptography;
using System.Text;
using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Users;

namespace CoreFoundry.UnitTests.Auth;

/// <summary>In-memory stand-ins for the Infrastructure adapters, wired into a real <see cref="AuthService"/>.</summary>
internal sealed class AuthHarness
{
    public AuthHarness()
    {
        UnitOfWork = new FakeUnitOfWork(this);
        Service = new AuthService(Users, Tokens, UnitOfWork, Hasher, TokenService, Clock);
    }

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    public FakeUsers Users { get; } = new();
    public FakeRefreshTokens Tokens { get; } = new();
    public FakeHasher Hasher { get; } = new();
    public FakeTokenService TokenService => _tokenService ??= new FakeTokenService(Clock);
    public FakeUnitOfWork UnitOfWork { get; }
    public AuthService Service { get; }

    private FakeTokenService? _tokenService;

    public RefreshToken StoredToken(string rawValue) =>
        Tokens.All.Single(token => token.TokenHash == FakeTokenService.Sha256Hex(rawValue));
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeUsers : IUserRepository
{
    public List<User> All { get; } = [];

    public Task<User?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(All.SingleOrDefault(user => user.Id == id));

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(All.SingleOrDefault(user => user.Email == normalizedEmail));

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(All.Any(user => user.Email == normalizedEmail));

    public Task<IReadOnlyList<User>> ListByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<User>>([.. All.Where(user => ids.Contains(user.Id))]);

    public void Add(User user) => All.Add(user);
}

internal sealed class FakeRefreshTokens : IRefreshTokenRepository
{
    public List<RefreshToken> All { get; } = [];

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(All.SingleOrDefault(token => token.TokenHash == tokenHash));

    public Task<IReadOnlyList<RefreshToken>> ListSuccessorsAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        var chain = new List<RefreshToken>();
        for (var next = token.ReplacedByToken; next is not null; next = next.ReplacedByToken)
        {
            chain.Add(next);
        }

        return Task.FromResult<IReadOnlyList<RefreshToken>>(chain);
    }

    public void Add(RefreshToken token) => All.Add(token);
}

/// <summary>Assigns ids like the database would; can simulate losing a concurrency race.</summary>
internal sealed class FakeUnitOfWork(AuthHarness harness) : IUnitOfWork
{
    private long _nextId = 1;

    public bool FailNextSaveWithConcurrencyConflict { get; set; }

    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailNextSaveWithConcurrencyConflict)
        {
            FailNextSaveWithConcurrencyConflict = false;
            throw new ConcurrencyConflictException();
        }

        SaveCount++;
        foreach (var user in harness.Users.All.Where(user => user.Id == 0))
        {
            typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, _nextId++);
        }

        foreach (var token in harness.Tokens.All.Where(token => token.Id == 0))
        {
            typeof(RefreshToken).GetProperty(nameof(RefreshToken.Id))!.SetValue(token, _nextId++);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeHasher : IPasswordHasher
{
    public int HashCalls { get; private set; }

    /// <summary>When set, a correct password verifies as <see cref="PasswordCheck.SuccessRehashNeeded"/>.</summary>
    public bool ReportRehashNeeded { get; set; }

    public string Hash(string password)
    {
        HashCalls++;
        return $"v2:{password}";
    }

    public PasswordCheck Verify(string passwordHash, string password) =>
        passwordHash.EndsWith($":{password}", StringComparison.Ordinal)
            ? ReportRehashNeeded ? PasswordCheck.SuccessRehashNeeded : PasswordCheck.Success
            : PasswordCheck.Failed;
}

internal sealed class FakeTokenService(TimeProvider clock) : ITokenService
{
    private int _counter;

    public AccessToken CreateAccessToken(User user) =>
        new($"access-{user.Id}-{++_counter}", clock.GetUtcNow().UtcDateTime.AddMinutes(15));

    public IssuedRefreshToken CreateRefreshToken()
    {
        var raw = $"refresh-{++_counter}";
        return new IssuedRefreshToken(raw, Sha256Hex(raw), clock.GetUtcNow().UtcDateTime.AddDays(7));
    }

    public string HashRefreshToken(string rawValue) => Sha256Hex(rawValue);

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
