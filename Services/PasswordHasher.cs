using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebDS_Test.Services;

/// <summary>Contract for hashing and verifying customer passwords.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing, stored as <c>iterations.saltBase64.hashBase64</c>.
/// Hand-rolled because <c>Microsoft.AspNetCore.Identity</c>'s <c>PasswordHasher&lt;T&gt;</c> is
/// available but its EF Core store package is not cached on this machine; this keeps the
/// credential format explicit and self-contained.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int DefaultIterations = 210_000; // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    private const char Separator = '.';

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, DefaultIterations, HashAlgorithmName.SHA256, KeySize);

        return string.Join(Separator, DefaultIterations, Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        var parts = hash.Split(Separator);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) ||
            iterations <= 0)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Constant-time compare: a length-dependent early exit would leak information.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
