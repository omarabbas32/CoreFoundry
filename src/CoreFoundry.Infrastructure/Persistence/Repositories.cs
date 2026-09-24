using CoreFoundry.Application.Auth;
using CoreFoundry.Application.Common;
using CoreFoundry.Domain.Users;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace CoreFoundry.Infrastructure.Persistence;

internal sealed class UserRepository(MetadataDbContext db) : IUserRepository
{
    public Task<User?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(user => user.Email == normalizedEmail, cancellationToken);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(user => user.Email == normalizedEmail, cancellationToken);

    public async Task<IReadOnlyList<User>> ListByIdsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        await db.Users.Where(user => ids.Contains(user.Id)).ToListAsync(cancellationToken);

    public void Add(User user) => db.Users.Add(user);
}

internal sealed class RefreshTokenRepository(MetadataDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ListSuccessorsAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        // Loads all of this user's tokens and walks the chain in memory. Tokens accumulate (one per login/refresh)
        // until an expired-token cleanup exists; fine at this scale, noted for M5 hardening.
        var byId = await db.RefreshTokens
            .Where(candidate => candidate.UserId == token.UserId)
            .ToDictionaryAsync(candidate => candidate.Id, cancellationToken);

        var chain = new List<RefreshToken>();
        var visited = new HashSet<long> { token.Id };
        var next = token.ReplacedByTokenId;
        while (next is long id && visited.Add(id) && byId.TryGetValue(id, out var successor))
        {
            chain.Add(successor);
            next = successor.ReplacedByTokenId;
        }

        return chain;
    }

    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);
}

internal sealed class EfUnitOfWork(MetadataDbContext db) : IUnitOfWork
{
    /// <summary>MySQL ER_DUP_ENTRY.</summary>
    private const int DuplicateKeyError = 1062;

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The data was changed by another request.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: DuplicateKeyError })
        {
            // A unique index caught a race the service's own "already exists" check couldn't see.
            throw new ConflictException("Something with the same name was just created by another request.", ex);
        }
    }
}
