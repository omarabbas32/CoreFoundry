namespace CoreFoundry.Domain.Schema;

/// <summary>Column types a user can pick. Stored as TINYINT; the MySQL mapping lives in the SQL renderer (M3).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "Members are SQL column types shown to users, not .NET types.")]
public enum DataType : byte
{
    Int = 1,
    BigInt = 2,
    Decimal = 3,
    Bool = 4,
    Varchar = 5,
    Text = 6,
    DateTime = 7,
    Date = 8,
    Json = 9,
    Uuid = 10,
}
