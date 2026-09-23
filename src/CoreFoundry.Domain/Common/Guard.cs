namespace CoreFoundry.Domain.Common;

internal static class Guard
{
    public static string NotBlank(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static long PositiveId(long value, string name) =>
        value > 0 ? value : throw new DomainException($"{name} must be a positive id.");

    public static DateTime Utc(DateTime value, string name) =>
        value.Kind == DateTimeKind.Utc ? value : throw new DomainException($"{name} must be in UTC.");
}
