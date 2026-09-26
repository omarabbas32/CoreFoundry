using System.Security.Cryptography;
using CoreFoundry.Application.Assistant;
using Microsoft.AspNetCore.DataProtection;

namespace CoreFoundry.Infrastructure.Ai;

/// <summary>Encrypts users' own AI keys with ASP.NET Core Data Protection (its keys live in the metadata database).</summary>
public sealed class DataProtectionAiKeyProtector(IDataProtectionProvider provider) : IAiKeyProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("CoreFoundry.AiKey.v1");

    public string Protect(string apiKey) => _protector.Protect(apiKey);

    public string Unprotect(string ciphertext)
    {
        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            throw new AiProviderException(AiFailure.KeyRejected, "Your saved xAI key can't be read any more. Add it again.", ex);
        }
    }
}
