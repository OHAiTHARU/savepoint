using System.Security.Cryptography;
using System.Text;

namespace Savepoint.Core.State;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

/// <summary>DPAPI, current-user scope: only this Windows account on this PC can decrypt.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Savepoint.Agent.ApiKey");

    public string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is Windows-only");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(data);
    }

    public string Unprotect(string protectedText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is Windows-only");
        if (!protectedText.StartsWith("dpapi:", StringComparison.Ordinal))
            throw new CryptographicException("Not a DPAPI value");
        var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText[6..]), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}

/// <summary>No encryption. Only for tests and non-Windows development.</summary>
public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plaintext) => "plain:" + plaintext;
    public string Unprotect(string protectedText) =>
        protectedText.StartsWith("plain:", StringComparison.Ordinal) ? protectedText[6..] : protectedText;
}

public static class SecretProtectors
{
    public static ISecretProtector ForPlatform() =>
        OperatingSystem.IsWindows() ? new DpapiProtector() : new PlainProtector();
}
