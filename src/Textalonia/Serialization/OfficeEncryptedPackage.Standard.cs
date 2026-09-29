using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Textalonia.Serialization;

internal static partial class OfficeEncryptedPackage
{
    /// <summary>
    /// MS-OFFCRYPTO 2.3.4.5 through 2.3.4.9. Standard encryption has a password verifier,
    /// but no package authentication tag. The caller must still validate the ZIP.
    /// </summary>
    private static byte[] DecryptStandard(byte[] info, byte[] package, string password, CancellationToken token)
    {
        if (info.Length < 12 + 32 + 72 || U16(info, 2) != 2 || U16(info, 0) is not (2 or 3 or 4))
            throw new FormatException("Standard EncryptionInfo is incomplete.");
        var flags = U32(info, 4);
        var headerSize = U32(info, 8);
        if (flags != 0x24 || headerSize < 34 || headerSize > info.Length - 12 - 72 ||
            U32(info, 12) != flags || U32(info, 16) != 0 || U32(info, 24) != 0x8004 ||
            U32(info, 32) != 24 || U32(info, 40) != 0)
            throw new NotSupportedException("Unsupported Standard Office encryption parameters.");
        var keyBits = U32(info, 28);
        if ((U32(info, 20), keyBits) is not ((0x660e, 128) or (0x660f, 192) or (0x6610, 256)))
            throw new NotSupportedException("Unsupported Standard Office AES key size.");
        var verifierOffset = checked(12 + (int)headerSize);
        if (verifierOffset + 72 != info.Length || U32(info, verifierOffset) != 16 ||
            U32(info, verifierOffset + 36) != 20)
            throw new FormatException("Standard Office password verifier has an invalid size.");
        var csp = info.AsSpan(44, verifierOffset - 44);
        if (csp.Length < 2 || csp.Length % 2 != 0 || csp[^1] != 0 || csp[^2] != 0)
            throw new FormatException("Standard Office CSP name is invalid.");
        _ = new UnicodeEncoding(false, false, true).GetString(csp);
        var salt = info.AsSpan(verifierOffset + 4, 16);
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[]? key = null;
        try
        {
            key = DeriveStandardKey(salt, passwordBytes, (int)keyBits / 8, token);
            var verifier = AesEcb(info.AsSpan(verifierOffset + 20, 16), key);
            var expected = AesEcb(info.AsSpan(verifierOffset + 40, 32), key);
            var actual = SHA1.HashData(verifier);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected.AsSpan(0, 20)))
                throw new CryptographicException("Incorrect Office package password or damaged verifier.");

            if (package.Length < 24) throw new FormatException("EncryptedPackage is incomplete.");
            var size64 = BinaryPrimitives.ReadUInt64LittleEndian(package.AsSpan(0, 8));
            if (size64 < 4 || size64 > MaxPackageBytes)
                throw new FormatException("EncryptedPackage plaintext size is invalid.");
            var size = (int)size64;
            if (package.Length != checked(8 + Round16(size)))
                throw new FormatException("EncryptedPackage ciphertext size is invalid.");
            token.ThrowIfCancellationRequested();
            var padded = AesEcb(package.AsSpan(8), key);
            var plaintext = padded.AsSpan(0, size).ToArray();
            CryptographicOperations.ZeroMemory(padded);
            if (!plaintext.AsSpan().StartsWith("PK"u8))
                throw new FormatException("Decrypted Office package is not a ZIP archive.");
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveStandardKey(ReadOnlySpan<byte> salt, byte[] password, int keyBytes, CancellationToken token)
    {
        var initial = new byte[salt.Length + password.Length];
        salt.CopyTo(initial);
        password.CopyTo(initial, salt.Length);
        var hash = SHA1.HashData(initial);
        CryptographicOperations.ZeroMemory(initial);
        var iteration = new byte[24];
        for (var i = 0; i < 50_000; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt32LittleEndian(iteration, (uint)i);
            hash.CopyTo(iteration, 4);
            var next = SHA1.HashData(iteration);
            CryptographicOperations.ZeroMemory(hash);
            hash = next;
        }
        CryptographicOperations.ZeroMemory(iteration);
        var block = new byte[24];
        hash.CopyTo(block, 0);
        var finalHash = SHA1.HashData(block);
        CryptographicOperations.ZeroMemory(hash);
        CryptographicOperations.ZeroMemory(block);
        var x1Input = Enumerable.Repeat((byte)0x36, 64).ToArray();
        var x2Input = Enumerable.Repeat((byte)0x5c, 64).ToArray();
        for (var i = 0; i < finalHash.Length; i++)
        {
            x1Input[i] ^= finalHash[i];
            x2Input[i] ^= finalHash[i];
        }
        CryptographicOperations.ZeroMemory(finalHash);
        var x1 = SHA1.HashData(x1Input);
        var x2 = SHA1.HashData(x2Input);
        CryptographicOperations.ZeroMemory(x1Input);
        CryptographicOperations.ZeroMemory(x2Input);
        var key = new byte[keyBytes];
        x1.AsSpan(0, Math.Min(keyBytes, x1.Length)).CopyTo(key);
        if (keyBytes > x1.Length) x2.AsSpan(0, keyBytes - x1.Length).CopyTo(key.AsSpan(x1.Length));
        CryptographicOperations.ZeroMemory(x1);
        CryptographicOperations.ZeroMemory(x2);
        return key;
    }

    private static int Round16(int size) => checked((size + 15) / 16 * 16);

    private static byte[] AesEcb(ReadOnlySpan<byte> ciphertext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(ciphertext, PaddingMode.None);
    }
}
