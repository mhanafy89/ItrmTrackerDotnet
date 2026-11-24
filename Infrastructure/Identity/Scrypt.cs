using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ItrmTracker.Infrastructure.Identity;

/// <summary>
/// scrypt key derivation (RFC 7914) implemented on the BCL (PBKDF2-HMAC-SHA256 + Salsa20/8).
/// Used only to VERIFY password hashes created by the legacy Node.js app
/// (<c>crypto.scryptSync(password, saltHex, 64)</c> with Node defaults N=16384, r=8, p=1).
/// </summary>
public static class Scrypt
{
    public const int DefaultCost = 16384;
    public const int DefaultBlockSize = 8;
    public const int DefaultParallelization = 1;
    public const int KeyLength = 64;

    /// <summary>Derives a key; <paramref name="cost"/> (N) must be a power of two greater than 1.</summary>
    public static byte[] DeriveKey(byte[] password, byte[] salt, int cost = DefaultCost, int blockSize = DefaultBlockSize, int parallel = DefaultParallelization, int keyLength = KeyLength)
    {
        if (cost < 2 || (cost & (cost - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(cost), "N must be a power of two greater than 1.");
        var blockBytes = 128 * blockSize;
        var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, parallel * blockBytes);

        var words = 32 * blockSize;
        var x = new uint[words];
        var v = new uint[words * cost];
        var tmp = new uint[16];
        var y = new uint[words];
        for (var i = 0; i < parallel; i++)
        {
            var offset = i * blockBytes;
            for (var k = 0; k < words; k++) x[k] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset + k * 4, 4));
            RoMix(x, v, y, tmp, blockSize, cost);
            for (var k = 0; k < words; k++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset + k * 4, 4), x[k]);
        }
        var result = Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, keyLength);
        CryptographicOperations.ZeroMemory(b);
        Array.Clear(v);
        return result;
    }

    /// <summary>Verifies a legacy <c>saltHex$hashHex</c> string (constant-time comparison).</summary>
    public static bool VerifyLegacy(string password, string stored)
    {
        var parts = (stored ?? string.Empty).Split('$');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }
        // Node passes the hex salt STRING to scrypt, i.e. its UTF-8 bytes – not the decoded bytes.
        var candidate = DeriveKey(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(parts[0]), keyLength: KeyLength);
        return candidate.Length == expected.Length && CryptographicOperations.FixedTimeEquals(candidate, expected);
    }

    /// <summary>Creates a hash in the legacy format (used when the app is configured to stay compatible with the Node.js app).</summary>
    public static string HashLegacy(string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var hash = DeriveKey(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt));
        return $"{salt}${Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>Returns true when the string looks like a legacy Node.js scrypt hash.</summary>
    public static bool IsLegacyFormat(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.Count(c => c == '$') == 1 && stored.Split('$').All(p => p.Length > 0 && p.All(Uri.IsHexDigit));

    private static void RoMix(uint[] x, uint[] v, uint[] y, uint[] tmp, int r, int n)
    {
        var words = 32 * r;
        for (var i = 0; i < n; i++)
        {
            Array.Copy(x, 0, v, i * words, words);
            BlockMix(x, y, tmp, r);
        }
        for (var i = 0; i < n; i++)
        {
            var j = (int)(x[(2 * r - 1) * 16] & (uint)(n - 1));
            var vOffset = j * words;
            for (var k = 0; k < words; k++) x[k] ^= v[vOffset + k];
            BlockMix(x, y, tmp, r);
        }
    }

    private static void BlockMix(uint[] b, uint[] y, uint[] x, int r)
    {
        Array.Copy(b, (2 * r - 1) * 16, x, 0, 16);
        for (var i = 0; i < 2 * r; i++)
        {
            for (var k = 0; k < 16; k++) x[k] ^= b[i * 16 + k];
            Salsa208(x);
            // Even blocks go to the first half, odd blocks to the second half.
            var dest = (i / 2 + (i % 2) * r) * 16;
            Array.Copy(x, 0, y, dest, 16);
        }
        Array.Copy(y, b, 32 * r);
    }

    private static uint R(uint a, int b) => (a << b) | (a >> (32 - b));

    private static void Salsa208(uint[] b)
    {
        uint x0 = b[0], x1 = b[1], x2 = b[2], x3 = b[3], x4 = b[4], x5 = b[5], x6 = b[6], x7 = b[7],
            x8 = b[8], x9 = b[9], x10 = b[10], x11 = b[11], x12 = b[12], x13 = b[13], x14 = b[14], x15 = b[15];
        for (var i = 0; i < 8; i += 2)
        {
            x4 ^= R(x0 + x12, 7); x8 ^= R(x4 + x0, 9); x12 ^= R(x8 + x4, 13); x0 ^= R(x12 + x8, 18);
            x9 ^= R(x5 + x1, 7); x13 ^= R(x9 + x5, 9); x1 ^= R(x13 + x9, 13); x5 ^= R(x1 + x13, 18);
            x14 ^= R(x10 + x6, 7); x2 ^= R(x14 + x10, 9); x6 ^= R(x2 + x14, 13); x10 ^= R(x6 + x2, 18);
            x3 ^= R(x15 + x11, 7); x7 ^= R(x3 + x15, 9); x11 ^= R(x7 + x3, 13); x15 ^= R(x11 + x7, 18);
            x1 ^= R(x0 + x3, 7); x2 ^= R(x1 + x0, 9); x3 ^= R(x2 + x1, 13); x0 ^= R(x3 + x2, 18);
            x6 ^= R(x5 + x4, 7); x7 ^= R(x6 + x5, 9); x4 ^= R(x7 + x6, 13); x5 ^= R(x4 + x7, 18);
            x11 ^= R(x10 + x9, 7); x8 ^= R(x11 + x10, 9); x9 ^= R(x8 + x11, 13); x10 ^= R(x9 + x8, 18);
            x12 ^= R(x15 + x14, 7); x13 ^= R(x12 + x15, 9); x14 ^= R(x13 + x12, 13); x15 ^= R(x14 + x13, 18);
        }
        b[0] += x0; b[1] += x1; b[2] += x2; b[3] += x3; b[4] += x4; b[5] += x5; b[6] += x6; b[7] += x7;
        b[8] += x8; b[9] += x9; b[10] += x10; b[11] += x11; b[12] += x12; b[13] += x13; b[14] += x14; b[15] += x15;
    }
}
