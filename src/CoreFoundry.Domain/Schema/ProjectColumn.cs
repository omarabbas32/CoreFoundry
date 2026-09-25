namespace CoreFoundry.Domain.Schema;

/// <summary>
/// A column in the <b>draft</b> schema. Its stable <see cref="Id"/> plus <see cref="AppliedName"/>
/// is what lets the schema engine tell a rename from a drop + add.
/// </summary>
/// <remarks>Created and changed only through <see cref="ProjectTable"/>, which enforces the table-wide rules.</remarks>
public sealed class ProjectColumn
{
    public const int NameMaxLength = IdentifierRules.MaxLength;
    public const int DefaultValueMaxLength = 255;

    private ProjectColumn() { } // EF Core

    internal ProjectColumn(string name, ColumnDefinition definition, int ordinalPosition)
    {
        Name = name;
        Redefine(definition);
        OrdinalPosition = ordinalPosition;
    }

    public long Id { get; private set; }
    public long TableId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? AppliedName { get; private set; }
    public DataType DataType { get; private set; }
    public int? Length { get; private set; }
    public byte? Precision { get; private set; }
    public byte? Scale { get; private set; }
    public bool IsNullable { get; private set; }
    public bool IsUnique { get; private set; }

    /// <summary>The canonical form of <see cref="Default"/>, or null for no default.</summary>
    public string? DefaultValue { get; private set; }

    /// <summary>The table whose <c>id</c> this column references (a foreign key), or null.</summary>
    public long? ReferencesTableId { get; private set; }

    /// <summary>Set exactly when <see cref="ReferencesTableId"/> is.</summary>
    public ReferenceAction? OnDelete { get; private set; }

    public int OrdinalPosition { get; private set; }
    public bool PendingDrop { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public bool IsApplied => AppliedName is not null;

    /// <summary>The default parsed into its typed form.</summary>
    public ColumnDefault? Default => ColumnDefault.Parse(DataType, DefaultValue, Length, Precision, Scale);

    public ColumnDefinition Definition =>
        new(DataType, Length, Precision, Scale, IsNullable, IsUnique, Default, ReferencesTableId, OnDelete);

    internal void Rename(string name) => Name = name;

    internal void Redefine(ColumnDefinition definition)
    {
        DataType = definition.DataType;
        Length = definition.Length;
        Precision = definition.Precision;
        Scale = definition.Scale;
        IsNullable = definition.IsNullable;
        IsUnique = definition.IsUnique;
        DefaultValue = definition.Default?.Canonical;
        ReferencesTableId = definition.ReferencesTableId;
        OnDelete = definition.OnDelete;
    }

    internal void MoveTo(int ordinalPosition) => OrdinalPosition = ordinalPosition;

    internal void SetPendingDrop(bool pendingDrop) => PendingDrop = pendingDrop;
}
