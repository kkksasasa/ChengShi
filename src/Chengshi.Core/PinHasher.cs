using System.Security.Cryptography;
using System.Text;

namespace Chengshi.Core;

public static class PinHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>校验时允许的最低迭代次数：防止被篡改过的存储串把迭代数改成 1，让暴力破解变快。</summary>
    private const int MinVerifyIterations = 10_000;

    public static string NormalizePin(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var trimmed = pin.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (character is >= '０' and <= '９')
            {
                builder.Append((char)('0' + (character - '０')));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static string NormalizeRecovery(string token)
    {
        var normalized = NormalizePin(token);
        return normalized.Replace("-", string.Empty, StringComparison.Ordinal);
    }

    public static string Hash(string pin)
    {
        var normalized = NormalizePin(pin);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string pin, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var normalized = NormalizePin(pin);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            {
                return false;
            }

            if (iterations < MinVerifyIterations)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(normalized),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
