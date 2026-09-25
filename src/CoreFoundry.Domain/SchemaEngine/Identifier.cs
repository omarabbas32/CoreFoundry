using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreFoundry.Domain.SchemaEngine;

/// <summary>
/// A name that is safe to put in DDL: <c>[a-z][a-z0-9_]</c>, at most 64 characters. The SQL renderer
/// only quotes <see cref="Identifier"/>s, never raw strings, so even a name that somehow skipped
/// validation can't reach MySQL (defense in depth behind <see cref="Schema.IdentifierRules"/>).
/// </summary>
public sealed partial record Identifier
{
    public const int MaxLength = 64;

    private Identifier(string value) => Value = value;

    public string Value { get; }

    /// <exception cref="ArgumentException">The name isn't a safe identifier.</exception>
    public static Identifier Of(string name) =>
        name is not null && Pattern().IsMatch(name)
            ? new Identifier(name)
            : throw new ArgumentException($"'{name}' is not a safe SQL identifier.", nameof(name));

    public static bool IsSafe(string? name) => name is not null && Pattern().IsMatch(name);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Names of the unique keys and foreign keys CoreFoundry creates.</summary>
public static class ConstraintNames
{
    /// <summary><c>uq_&lt;table&gt;_&lt;column&gt;</c>, shortened with a hash when longer than 64.</summary>
    public static string UniqueKey(string table, string column) => Named("uq", table, column);

    /// <summary><c>fk_&lt;table&gt;_&lt;column&gt;</c>, shortened with a hash when longer than 64.</summary>
    public static string ForeignKey(string table, string column) => Named("fk", table, column);

    private static string Named(string prefix, string table, string column)
    {
        var full = $"{prefix}_{table}_{column}";
        if (full.Length <= Identifier.MaxLength)
        {
            return full;
        }

        // Keep a readable start and make it unique with 8 hex characters of the full name's hash.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8];
        return $"{full[..(Identifier.MaxLength - 9)].TrimEnd('_')}_{hash}";
    }
}
