using CoreFoundry.Application.Common;

namespace CoreFoundry.Application.Data;

/// <summary>How to order a page of rows. <see cref="Column"/> null means the system <c>id</c>.</summary>
public sealed record SortOrder(DataColumn? Column, bool Descending)
{
    public static SortOrder ById { get; } = new(null, false);

    /// <summary>
    /// <c>title</c>, <c>-price_usd</c> or <c>id</c>. The name is only looked up in the table, never
    /// put into SQL itself.
    /// </summary>
    /// <exception cref="ValidationFailedException">Not a column of <paramref name="table"/>.</exception>
    public static SortOrder Parse(DataTable table, string? text)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (string.IsNullOrEmpty(text))
        {
            return ById;
        }

        var descending = text.StartsWith('-');
        var name = descending ? text[1..] : text;
        if (name == "id")
        {
            return new SortOrder(null, descending);
        }

        return table.FindColumn(name) is { } column
            ? new SortOrder(column, descending)
            : throw new ValidationFailedException("sort", $"Sort by id or a column of {table.Name}, optionally with a leading '-' for descending.");
    }
}
