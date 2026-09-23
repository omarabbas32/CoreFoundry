namespace CoreFoundry.Domain.Common;

/// <summary>A business rule was violated. The message is safe to show to the caller.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message) { }

    public DomainException() { }

    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}
