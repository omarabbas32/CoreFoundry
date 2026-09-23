namespace CoreFoundry.Domain.Projects;

/// <summary>Where the project is in its lifecycle. Stored as TINYINT.</summary>
public enum ProjectStatus : byte
{
    /// <summary>Metadata saved; <c>CREATE DATABASE</c> not yet confirmed.</summary>
    Provisioning = 0,
    Active = 1,
    /// <summary><c>CREATE DATABASE</c> failed; can be retried.</summary>
    Failed = 2,
    /// <summary>Hidden from lists; <c>DROP DATABASE</c> and metadata cleanup pending.</summary>
    Deleting = 3,
}
