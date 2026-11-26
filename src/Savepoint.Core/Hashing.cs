using System.Security.Cryptography;

namespace Savepoint.Core;

public static class Hashing
{
    /// <summary>Open for reading while letting the game keep writing/deleting.</summary>
    public static FileStream OpenShared(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 1 << 16,
            Options = FileOptions.SequentialScan,
        });

    public static string Sha256File(string path)
    {
        using var fs = OpenShared(path);
        return Sha256Stream(fs);
    }

    public static string Sha256Stream(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    public static string Sha256Bytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
