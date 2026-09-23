namespace CoreFoundry.Domain.Schema;

/// <summary>Stored as TINYINT.</summary>
public enum MigrationStatus : byte
{
    Pending = 0,
    Applied = 1,
    Failed = 2,
}
