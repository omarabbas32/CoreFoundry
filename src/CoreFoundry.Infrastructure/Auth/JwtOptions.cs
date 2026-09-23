using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace CoreFoundry.Infrastructure.Auth;

/// <summary>Bound from the <c>Jwt</c> configuration section; the signing key comes from user-secrets / environment.</summary>
public sealed class JwtOptions : IValidatableObject
{
    public const string SectionName = "Jwt";

    /// <summary>HS256 needs a key of at least 256 bits.</summary>
    public const int MinSigningKeyBytes = 32;

    [Required]
    public string Issuer { get; set; } = "corefoundry";

    [Required]
    public string Audience { get; set; } = "corefoundry-web";

    [Required]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));

    /// <summary>Shared by token creation and the JwtBearer handler so both always agree.</summary>
    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = CreateSigningKey(),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < MinSigningKeyBytes)
        {
            yield return new ValidationResult(
                $"Jwt:SigningKey must be at least {MinSigningKeyBytes} bytes. " +
                "Set it with `dotnet user-secrets set \"Jwt:SigningKey\" \"...\"` in src/CoreFoundry.Api.",
                [nameof(SigningKey)]);
        }
    }
}
