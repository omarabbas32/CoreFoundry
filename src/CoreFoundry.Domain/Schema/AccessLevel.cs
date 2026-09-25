namespace CoreFoundry.Domain.Schema;

/// <summary>Who may read or write a table in the exported API. Stored as TINYINT.</summary>
public enum AccessLevel : byte
{
    /// <summary>Anyone, no token required.</summary>
    Public = 1,

    /// <summary>Any user with a valid token.</summary>
    SignedIn = 2,

    /// <summary>Users with the Admin role.</summary>
    Admin = 3,
}
