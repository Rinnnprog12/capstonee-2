using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// Disk-backed blob store for local development (no Azurite).
/// Read URLs are HMAC-signed so CALSV/ML can download without JWT.
/// </summary>
public sealed class LocalBlobAccess
{
    private readonly byte[] _key;

    public LocalBlobAccess(IConfiguration config, IHostEnvironment env)
    {
        var relative = config["AzureBlob:LocalRoot"] ?? "App_Data/blobs";
        Root = Path.GetFullPath(Path.Combine(env.ContentRootPath, relative));
        Directory.CreateDirectory(Root);

        PublicBaseUrl = (config["AzureBlob:PublicBaseUrl"] ?? "http://localhost:5200").TrimEnd('/');
        var secret = config["Jwt:Key"] ?? "DEV_ONLY_CHANGE_ME_TSUORG_SUPER_SECRET_KEY_32+";
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string Root { get; }
    public string PublicBaseUrl { get; }

    public string CreateReadUrl(string blobPath, TimeSpan expiry)
    {
        var exp = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds();
        var sig = Sign(blobPath, exp);
        return $"{PublicBaseUrl}/api/v1/internal/blobs?p={Uri.EscapeDataString(Encode(blobPath))}&exp={exp}&sig={Uri.EscapeDataString(sig)}";
    }

    public bool TryValidate(string encodedPath, long exp, string? sig, out string blobPath)
    {
        blobPath = "";
        if (string.IsNullOrWhiteSpace(encodedPath) || string.IsNullOrWhiteSpace(sig))
            return false;
        if (exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;

        try
        {
            blobPath = Decode(encodedPath);
        }
        catch
        {
            return false;
        }

        if (ContainsTraversal(blobPath))
            return false;

        var expected = Sign(blobPath, exp);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(sig));
    }

    public string ResolvePhysicalPath(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || ContainsTraversal(blobPath))
            throw new InvalidOperationException("Invalid blob path.");

        var root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(Root, blobPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid blob path.");
        return full;
    }

    private string Sign(string blobPath, long exp)
    {
        var payload = Encoding.UTF8.GetBytes($"{exp}\n{blobPath}");
        var hash = HMACSHA256.HashData(_key, payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool ContainsTraversal(string path) =>
        path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path);

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
