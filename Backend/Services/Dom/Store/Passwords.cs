using System.Security.Cryptography;
using System.Text;

namespace Dom.Store;

/// <summary>
///     PBKDF2-SHA256. The password is never held — only the output and the parameters it was produced with, so
///     the cost can be raised later without invalidating everybody's password.
/// </summary>
internal static class Passwords
{
    /// <summary>OWASP's floor for PBKDF2-SHA256. Stored per user, so raising it is not a migration.</summary>
    private const int DefaultIterations = 210_000;

    /// <summary>A fresh salt, and what <paramref name="password" /> derives to under it, at today's cost.</summary>
    public static (string Salt, string Hash, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(Derive(password, salt, DefaultIterations)),
            DefaultIterations);
    }

    /// <summary>Whether <paramref name="password" /> is this account's.</summary>
    public static bool Verify(User user, string? password)
    {
        var expected = Convert.FromBase64String(user.Hash);
        var actual = Derive(password ?? "", Convert.FromBase64String(user.Salt), user.Iterations);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Re-hashes the account for a new password. Revoking its tokens is the caller's job.</summary>
    public static void Set(User user, string password)
    {
        (user.Salt, user.Hash, user.Iterations) = Hash(password);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA256, 32);
    }
}
