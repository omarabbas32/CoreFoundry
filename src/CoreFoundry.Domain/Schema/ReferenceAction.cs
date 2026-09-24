namespace CoreFoundry.Domain.Schema;

/// <summary>What happens to referencing rows when the referenced row is deleted. Stored as TINYINT.</summary>
public enum ReferenceAction : byte
{
    /// <summary>The delete is refused while rows still reference it.</summary>
    Restrict = 1,

    /// <summary>Referencing rows are deleted too.</summary>
    Cascade = 2,

    /// <summary>The referencing column is set to NULL (the column must be nullable).</summary>
    SetNull = 3,
}
