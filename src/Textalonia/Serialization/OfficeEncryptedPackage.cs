using System.Buffers.Binary;
using System.Text;

namespace Textalonia.Serialization;

/// <summary>
/// Recognizes the compound-file envelope used by password-encrypted Open XML packages.
/// It does not decrypt either the standard or agile Office encryption formats.
/// The FAT and root directory traversal follows MS-CFB sections 2.2, 2.3 and 2.6.
/// </summary>
internal static partial class OfficeEncryptedPackage
{
    private static ReadOnlySpan<byte> Signature => [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];
    private const uint EndOfChain = 0xfffffffe;
    private const uint FreeSector = 0xffffffff;
    private const uint FatSector = 0xfffffffd;
    private const uint DifatSector = 0xfffffffc;

    internal static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    /// <summary>
    /// Requires both root-level EncryptionInfo and EncryptedPackage streams. A mere CFB
    /// signature is insufficient: an ordinary legacy DOC file is also a compound file.
    /// Malformed CFB data is treated as unrecognized so the caller can reject its format.
    /// </summary>
    internal static bool IsEncrypted(byte[] data)
    {
        if (!IsCompoundFile(data) || data.Length < 512) return false;
        if (U16(data, 28) != 0xfffe || U16(data, 32) != 6 || U32(data, 56) != 0x1000) return false;
        var major = U16(data, 26);
        var sectorSize = major switch
        {
            3 when U16(data, 30) == 9 => 512,
            4 when U16(data, 30) == 12 => 4096,
            _ => 0
        };
        if (sectorSize == 0 || data.Length < sectorSize || data.Length % sectorSize != 0) return false;
        var sectorCount = data.Length / sectorSize - 1;
        if (sectorCount < 1) return false;
        var fatCount = U32(data, 44);
        var difatCount = U32(data, 72);
        if (fatCount == 0 || fatCount > sectorCount || difatCount > sectorCount) return false;
        if (major == 3 && U32(data, 40) != 0) return false;

        var fatSectors = new List<uint>((int)fatCount);
        var seenFat = new HashSet<uint>();
        bool AddFat(uint sector)
        {
            if (sector == FreeSector) return true;
            if (sector >= sectorCount || !seenFat.Add(sector)) return false;
            fatSectors.Add(sector);
            return fatSectors.Count <= fatCount;
        }

        for (var i = 0; i < 109; i++)
            if (!AddFat(U32(data, 76 + i * 4))) return false;
        var difat = U32(data, 68);
        var seenDifat = new HashSet<uint>();
        for (var i = 0u; i < difatCount; i++)
        {
            if (difat >= sectorCount || !seenDifat.Add(difat)) return false;
            var offset = SectorOffset(difat, sectorSize);
            for (var j = 0; j < sectorSize / 4 - 1; j++)
                if (!AddFat(U32(data, offset + j * 4))) return false;
            difat = U32(data, offset + sectorSize - 4);
        }
        if (difat != EndOfChain) return false;
        if (fatSectors.Count != fatCount) return false;
        // The FAT itself must identify each FAT and DIFAT sector correctly.
        foreach (var sector in fatSectors)
            if (!TryFatEntry(sector, out var marker) || marker != FatSector) return false;
        foreach (var sector in seenDifat)
            if (!TryFatEntry(sector, out var marker) || marker != DifatSector) return false;

        bool TryFatEntry(uint sector, out uint next)
        {
            var entriesPerSector = sectorSize / 4;
            if (sector >= sectorCount || sector / entriesPerSector >= fatSectors.Count)
            { next = 0; return false; }
            var fatSectorOffset = SectorOffset(fatSectors[(int)(sector / entriesPerSector)], sectorSize);
            next = U32(data, fatSectorOffset + (int)(sector % entriesPerSector) * 4);
            return true;
        }

        // Directory sectors are a regular FAT chain. Cap the chain and every index by
        // the input's actual sector count, independent of the header's claimed counts.
        var directoryBytes = new List<byte>();
        var current = U32(data, 48);
        var seenDirectory = new HashSet<uint>();
        while (current != EndOfChain)
        {
            if (current >= sectorCount || !seenDirectory.Add(current) || !TryFatEntry(current, out var next)) return false;
            var sector = data.AsSpan(SectorOffset(current, sectorSize), sectorSize);
            directoryBytes.AddRange(sector.ToArray());
            current = next;
        }
        if (directoryBytes.Count < 128) return false;
        var directory = directoryBytes.ToArray();
        var entryCount = directory.Length / 128;
        if (directory[66] != 5) return false; // Root storage is stream ID 0.
        var rootChild = U32(directory, 76);
        var pending = new Stack<uint>();
        if (rootChild != FreeSector) pending.Push(rootChild);
        var visited = new HashSet<uint>();
        var foundInfo = false;
        var foundPackage = false;
        while (pending.Count != 0)
        {
            var index = pending.Pop();
            if (index >= entryCount || !visited.Add(index)) return false;
            var offset = (int)index * 128;
            var entry = directory.AsSpan(offset, 128);
            if (entry[66] is not (1 or 2)) return false;
            var length = U16(entry, 64);
            if (length is < 2 or > 64 || (length & 1) != 0 || U16(entry, length - 2) != 0) return false;
            string name;
            try { name = new UnicodeEncoding(false, false, true).GetString(entry[..(length - 2)]); }
            catch (DecoderFallbackException) { return false; }
            if (entry[66] == 2)
            {
                if (string.Equals(name, "EncryptionInfo", StringComparison.OrdinalIgnoreCase)) foundInfo = true;
                if (string.Equals(name, "EncryptedPackage", StringComparison.OrdinalIgnoreCase)) foundPackage = true;
            }
            var left = U32(entry, 68);
            var right = U32(entry, 72);
            if (left != FreeSector) pending.Push(left);
            if (right != FreeSector) pending.Push(right);
        }
        return foundInfo && foundPackage;
    }

    private static int SectorOffset(uint sector, int sectorSize) => checked((int)((sector + 1) * (long)sectorSize));
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
}
