using System.Security.Cryptography;
using TsuOrg.Application.Common;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// PBKDF2-SHA256 password hashing — no extra NuGet dependency.
/// Format stored: {iterations}.{saltBase64}.{hashBase64}
/// </summary>
public sealed class PasswordHasherService : IPasswordHasher
{
    private const int Iterations   = 350_000;
    private const int SaltSize     = 16;
    private const int HashSize     = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        try
        {
            var parts = storedHash.Split('.');
            if (parts.Length != 3) return false;

            var iterations = int.Parse(parts[0]);
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual   = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, HashSize);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
