using System.Globalization;
using System.Text;

namespace CoreFoundry.Application.Export;

/// <summary>
/// C# names for exported code. Pure. Input names are CoreFoundry identifiers (<c>[a-z][a-z0-9_]*</c>),
/// so PascalCase output is always a valid C# identifier and never a keyword (keywords are lower-case).
/// </summary>
public static class CodeNames
{
    /// <summary>
    /// Type names the generated code itself uses or imports. An entity class with one of these names
    /// would clash (<c>Task</c> in every async controller, <c>AppUser</c> of the generated auth …).
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "Action", "Activity", "AppDbContext", "AppUser", "Array", "Assembly", "Attribute", "AuthController", "AuthService",
        "Boolean", "Buffer", "Console", "Controller", "ControllerBase", "Convert", "DateOnly", "DateTime", "DateTimeOffset",
        "Decimal", "DependencyInjection", "Dictionary", "Directory", "Encoding", "Enum", "Environment", "Exception", "File",
        "Func", "Guid", "HttpContext", "IResult", "Index", "Int32", "Int64", "JsonDocument", "JsonElement", "JwtOptions",
        "Lazy", "List", "Math", "Memory", "Migration", "ModelBuilder", "Monitor", "Nullable", "Object", "PageRequest",
        "PagedResult", "Path", "ProblemDetails", "Program", "Random", "Range", "Results", "Span", "String", "Task", "Thread",
        "Timer", "TimeSpan", "Tuple", "Type", "Uri", "ValidationException", "Version",
    };

    /// <summary><c>price_usd</c> → <c>PriceUsd</c>, <c>isbn13</c> → <c>Isbn13</c>.</summary>
    public static string Pascal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(part[0])).Append(part.AsSpan(1));
        }

        return builder.ToString();
    }

    /// <summary>
    /// English plural → singular for the common cases (<c>books</c>, <c>categories</c>, <c>addresses</c>,
    /// <c>boxes</c>). Words it doesn't recognize as plural (<c>status</c>, <c>people</c>, <c>data</c>) are kept.
    /// </summary>
    public static string Singular(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        static bool Ends(string text, string suffix) => text.EndsWith(suffix, StringComparison.Ordinal);

        if (word.Length <= 3)
        {
            return word;
        }

        if (Ends(word, "ies"))
        {
            return word[..^3] + "y";
        }

        if (Ends(word, "sses") || Ends(word, "xes") || Ends(word, "ches") || Ends(word, "shes"))
        {
            return word[..^2];
        }

        if (Ends(word, "ss") || Ends(word, "us") || Ends(word, "is") || !Ends(word, "s"))
        {
            return word;
        }

        return word[..^1];
    }

    /// <summary>
    /// A C# name for the solution and root namespace from the project's display name:
    /// <c>"My shop 2"</c> → <c>MyShop2</c>. Falls back to <c>Backend</c>.
    /// </summary>
    public static string Solution(string projectName)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        var builder = new StringBuilder();
        var upper = true;
        foreach (var character in projectName)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(upper ? char.ToUpperInvariant(character) : character);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        var name = builder.ToString();
        if (name.Length == 0)
        {
            return "Backend";
        }

        return char.IsAsciiDigit(name[0]) ? "App" + name : name;
    }

    /// <summary><c>name</c>, or <c>name2</c>, <c>name3</c> … whichever isn't taken yet; the result is added to <paramref name="taken"/>.</summary>
    public static string Unique(string name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++)
        {
            candidate = name + i.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }
}
