using System.Buffers.Binary;
using System.Text;

namespace Textalonia.Serialization;

internal static partial class OfficeEncryptedPackage
{
    private const int MaxPackageBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Decrypts password-based Standard AES/ECB packages. Other Office
    /// encryption schemes are rejected explicitly. No plaintext is returned before
    /// the password verifier succeeds. Standard encryption has no package authentication tag.
    /// </summary>
    internal static byte[] Decrypt(byte[] data, string password, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > 1024) throw new ArgumentOutOfRangeException(nameof(password));
        if (!IsEncrypted(data)) throw new FormatException("Input is not an encrypted Open XML package.");
        var (info, encryptedPackage) = ReadEncryptionStreams(data, token);
        if (info.Length < 8) throw new FormatException("EncryptionInfo is incomplete.");
        if (U16(info, 2) == 2 && U16(info, 0) is 2 or 3 or 4)
            return DecryptStandard(info, encryptedPackage, password, token);
        throw new NotSupportedException("Only Standard Office AES password encryption is supported.");
    }

    private static (byte[] Info, byte[] Package) ReadEncryptionStreams(byte[] data, CancellationToken token)
    {
        var sectorSize = 1 << U16(data, 30);
        var sectorCount = data.Length / sectorSize - 1;
        var fatCount = checked((int)U32(data, 44));
        var fatSectors = new List<uint>(fatCount);
        for (var i = 0; i < 109; i++)
        {
            var sector = U32(data, 76 + i * 4);
            if (sector != FreeSector) fatSectors.Add(sector);
        }
        var difat = U32(data, 68);
        for (var i = 0u; i < U32(data, 72); i++)
        {
            token.ThrowIfCancellationRequested();
            var offset = SectorOffset(difat, sectorSize);
            for (var j = 0; j < sectorSize / 4 - 1; j++)
            {
                var sector = U32(data, offset + j * 4);
                if (sector != FreeSector) fatSectors.Add(sector);
            }
            difat = U32(data, offset + sectorSize - 4);
        }
        if (fatSectors.Count != fatCount) throw new FormatException("Compound file FAT is invalid.");
        uint NextFat(uint sector)
        {
            if (sector >= sectorCount || sector / (sectorSize / 4) >= fatSectors.Count)
                throw new FormatException("Compound file stream points outside the FAT.");
            return U32(data, SectorOffset(fatSectors[(int)(sector / (sectorSize / 4))], sectorSize) +
                (int)(sector % (sectorSize / 4)) * 4);
        }
        byte[] ReadSectors(uint first, int size)
        {
            if (size == 0) return [];
            var result = new byte[size];
            var count = (size + sectorSize - 1) / sectorSize;
            var visited = new HashSet<uint>();
            var sector = first;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (sector >= sectorCount || !visited.Add(sector))
                    throw new FormatException("Compound file stream chain is invalid.");
                Array.Copy(data, SectorOffset(sector, sectorSize), result, i * sectorSize,
                    Math.Min(sectorSize, size - i * sectorSize));
                sector = NextFat(sector);
            }
            if (sector != EndOfChain) throw new FormatException("Compound file stream chain has excess sectors.");
            return result;
        }
        var directorySectors = new List<byte>();
        var directoryVisited = new HashSet<uint>();
        var current = U32(data, 48);
        while (current != EndOfChain)
        {
            token.ThrowIfCancellationRequested();
            if (current >= sectorCount || !directoryVisited.Add(current))
                throw new FormatException("Compound file directory chain is invalid.");
            directorySectors.AddRange(data.AsSpan(SectorOffset(current, sectorSize), sectorSize).ToArray());
            current = NextFat(current);
        }
        var directory = directorySectors.ToArray();
        if (directory.Length < 128) throw new FormatException("Compound file root directory is missing.");
        var rootSize = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(120, 8));
        if (rootSize > MaxPackageBytes) throw new FormatException("Compound file mini stream is too large.");
        var infoOffset = -1;
        var packageOffset = -1;
        var pending = new Stack<uint>();
        var rootChild = U32(directory, 76);
        if (rootChild != FreeSector) pending.Push(rootChild);
        var seen = new HashSet<uint>();
        while (pending.Count != 0)
        {
            var index = pending.Pop();
            if (index >= directory.Length / 128 || !seen.Add(index))
                throw new FormatException("Compound file directory tree is invalid.");
            var offset = checked((int)index * 128);
            var entry = directory.AsSpan(offset, 128);
            var length = U16(entry, 64);
            var name = new UnicodeEncoding(false, false, true).GetString(entry[..(length - 2)]);
            if (entry[66] == 2)
            {
                if (string.Equals(name, "EncryptionInfo", StringComparison.OrdinalIgnoreCase))
                {
                    if (infoOffset >= 0) throw new FormatException("Duplicate EncryptionInfo stream.");
                    infoOffset = offset;
                }
                if (string.Equals(name, "EncryptedPackage", StringComparison.OrdinalIgnoreCase))
                {
                    if (packageOffset >= 0) throw new FormatException("Duplicate EncryptedPackage stream.");
                    packageOffset = offset;
                }
            }
            var left = U32(entry, 68);
            var right = U32(entry, 72);
            if (left != FreeSector) pending.Push(left);
            if (right != FreeSector) pending.Push(right);
        }
        if (infoOffset < 0 || packageOffset < 0) throw new FormatException("Encryption streams are missing.");

        var miniFatCount = checked((int)U32(data, 64));
        if (miniFatCount > sectorCount) throw new FormatException("Compound file mini FAT is too large.");
        var miniFat = miniFatCount == 0 ? [] : ReadSectors(U32(data, 60), checked(miniFatCount * sectorSize));
        var miniStream = rootSize == 0 ? [] : ReadSectors(U32(directory, 116), checked((int)rootSize));
        byte[] ReadStream(int offset, int limit)
        {
            var size64 = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(offset + 120, 8));
            if (size64 > (ulong)limit) throw new FormatException("Encrypted Office stream exceeds its size limit.");
            var size = (int)size64;
            if (size >= U32(data, 56)) return ReadSectors(U32(directory, offset + 116), size);
            if (size == 0) return [];
            var result = new byte[size];
            var sector = U32(directory, offset + 116);
            var visited = new HashSet<uint>();
            var count = (size + 63) / 64;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (sector >= miniStream.Length / 64 || sector >= miniFat.Length / 4 || !visited.Add(sector))
                    throw new FormatException("Compound file mini stream chain is invalid.");
                Array.Copy(miniStream, checked((int)sector * 64), result, i * 64, Math.Min(64, size - i * 64));
                sector = U32(miniFat, checked((int)sector * 4));
            }
            if (sector != EndOfChain) throw new FormatException("Compound file mini stream has excess sectors.");
            return result;
        }
        return (ReadStream(infoOffset, 1024 * 1024), ReadStream(packageOffset, MaxPackageBytes));
    }

}
