using CoreFoundry.Domain.Common;

namespace CoreFoundry.Domain.Schema;

/// <summary>
/// A column in the <b>draft</b> schema. Its stable <see cref="Id"/> plus <see cref="AppliedName"/>
/// is what lets the schema engine tell a rename from a drop + add.
/// </summary>
/// <remarks>Per-type rules for Length / Precision / Scale / DefaultValue arrive with the table designer in M2.</remarks>
public sealed class ProjectColumn
{
    public const int NameMaxLength = 64;
    public const int DefaultValueMaxLength = 255;

    private ProjectColumn() { } // EF Core

    public ProjectColumn(long tableId, string name, DataType dataType, int ordinalPosition)
    {
        TableId = Guard.PositiveId(tableId, nameof(TableId));
        Name = Guard.NotBlank(name, nameof(Name), NameMaxLength);
        DataType = Enum.IsDefined(dataType) ? dataType : throw new DomainException("Unknown data type.");
        OrdinalPosition = ordinalPosition >= 0
            ? ordinalPosition
            : throw new DomainException("OrdinalPosition can't be negative.");
        IsNullable = true;
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
    public string? DefaultValue { get; private set; }
    public int OrdinalPosition { get; private set; }
    public bool PendingDrop { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public bool IsApplied => AppliedName is not null;
}
