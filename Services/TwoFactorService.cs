using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebDS_Test.Services;

/// <summary>
/// Generates and validates Time-based One-Time Passwords (TOTP) per RFC 6238,
/// and manages backup codes for account recovery.
/// </summary>
/// <remarks>
/// <para>
/// TOTP uses HMAC-SHA1 with a 30-second time step and 6-digit codes, matching the
/// algorithm used by Google Authenticator, Authy, Microsoft Authenticator, etc.
/// </para>
/// <para>
/// The secret is a random 20-byte value encoded as Base32 (32 chars). It is stored
/// on the user record and never displayed again after initial setup. Backup codes
/// are 8-character alphanumeric strings, stored as a JSON array.
/// </para>
/// </remarks>
public interface ITwoFactorService
{
    /// <summary>Generates a new TOTP secret and the corresponding otpauth URI for QR display.</summary>
    (string Secret, string QrCodeUri) GenerateSecret(string userEmail, string issuer = "LibanPost eShop");

    /// <summary>Validates a 6-digit TOTP code against the secret. Allows ±1 time step clock skew.</summary>
    bool ValidateCode(string secret, string code);

    /// <summary>Generates 10 single-use backup codes.</summary>
    string[] GenerateBackupCodes();

    /// <summary>Consumes a backup code. Returns true if the code was valid and unused.</summary>
    bool ConsumeBackupCode(ref string storedCodesJson, string code);

    /// <summary>Returns the current TOTP code for a secret (for testing/debugging).</summary>
    string GetCurrentCode(string secret);
}

/// <inheritdoc />
public sealed class TwoFactorService : ITwoFactorService
{
    private const int CodeDigits = 6;
    private const int TimeStepSeconds = 30;
    private const int BackupCodeCount = 10;
    private const int BackupCodeLength = 8;
    private static readonly string[] BackupCodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Select(c => c.ToString()).ToArray();

    public (string Secret, string QrCodeUri) GenerateSecret(string userEmail, string issuer = "LibanPost eShop")
    {
        var secretBytes = RandomNumberGenerator.GetBytes(20);
        var secret = Base32Encode(secretBytes);

        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(userEmail)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits={CodeDigits}&period={TimeStepSeconds}";

        return (secret, uri);
    }

    public bool ValidateCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (!int.TryParse(code, out var provided) || code.Length != 6)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timeSteps = new[] { now / 30, (now / 30) - 1, (now / 30) + 1 };

        foreach (var step in timeSteps)
        {
            var expected = ComputeTotp(secret, step);
            if (expected == provided)
            {
                return true;
            }
        }

        return false;
    }

    public string[] GenerateBackupCodes()
    {
        var codes = new string[BackupCodeCount];
        for (var i = 0; i < BackupCodeCount; i++)
        {
            var sb = new StringBuilder(BackupCodeLength);
            for (var j = 0; j < BackupCodeLength; j++)
            {
                sb.Append(BackupCodeChars[RandomNumberGenerator.GetInt32(BackupCodeChars.Length)]);
            }
            codes[i] = sb.ToString();
        }
        return codes;
    }

    public bool ConsumeBackupCode(ref string storedCodesJson, string code)
    {
        if (string.IsNullOrWhiteSpace(storedCodesJson) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var codes = JsonSerializer.Deserialize<string[]>(storedCodesJson) ?? Array.Empty<string>();
        var idx = Array.FindIndex(codes, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return false;
        }

        // Remove the used code
        var remaining = codes.Where((_, i) => i != idx).ToArray();
        storedCodesJson = JsonSerializer.Serialize(remaining);
        return true;
    }

    public string GetCurrentCode(string secret)
    {
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return ComputeTotp(secret, step).ToString("D6");
    }

    /// <summary>
    /// Computes the TOTP code for a specific time step. Exposed for testing clock skew tolerance.
    /// </summary>
    public int ComputeTotpForTest(string secret, long timeStep) => ComputeTotp(secret, timeStep);

    private static int ComputeTotp(string base32Secret, long timeStep)
    {
        var key = Base32Decode(base32Secret);
        var data = BitConverter.GetBytes(timeStep);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(data);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(data);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) |
                     ((hash[offset + 1] & 0xFF) << 16) |
                     ((hash[offset + 2] & 0xFF) << 8) |
                     (hash[offset + 3] & 0xFF);

        return binary % 1_000_000;
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = new StringBuilder();
        foreach (var b in data)
        {
            bits.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
        }

        var padding = (5 - bits.Length % 5) % 5;
        bits.Append('0', padding);

        var sb = new StringBuilder();
        for (var i = 0; i < bits.Length; i += 5)
        {
            var chunk = bits.ToString(i, 5);
            var index = Convert.ToInt32(chunk, 2);
            sb.Append(alphabet[index]);
        }

        // RFC 4648 padding (not strictly needed for TOTP secrets but kept for compliance)
        var padCount = (8 - sb.Length % 8) % 8;
        if (padCount > 0)
        {
            sb.Append('=', padCount);
        }

        return sb.ToString();
    }

    private static byte[] Base32Decode(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = base32.TrimEnd('=').ToUpperInvariant();
        var bits = new StringBuilder();
        foreach (var c in clean)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) continue;
            bits.Append(Convert.ToString(index, 2).PadLeft(5, '0'));
        }

        var bytes = new List<byte>();
        for (var i = 0; i + 8 <= bits.Length; i += 8)
        {
            var chunk = bits.ToString(i, 8);
            bytes.Add((byte)Convert.ToInt32(chunk, 2));
        }

        return bytes.ToArray();
    }
}