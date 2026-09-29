using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public sealed class OfficeEncryptedPackageTests
{
    [Fact]
    public async Task Libreoffice_encrypted_docx_is_identified_and_rejected_before_zip_parsing()
    {
        // Generated with LibreOffice Writer via UNO storeAsURL(FilterName =
        // "Office Open XML Text", Password = "Textalonia fixture password").
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Interchange", "libreoffice-encrypted.docx");
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(OfficeEncryptedPackage.IsEncrypted(bytes));
        using var stream = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => DocumentFormats.Docx.LoadAsync(stream));
        Assert.Contains("Password-encrypted Office", error.Message);
    }

    [Fact]
    public async Task Standard_password_loads_real_encrypted_docx_and_rejects_wrong_password()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Interchange", "libreoffice-encrypted.docx");
        var bytes = await File.ReadAllBytesAsync(path);
        using var correct = new MemoryStream(bytes);
        var document = await DocumentFormats.Docx.LoadWithPasswordAsync(correct, "Textalonia fixture password");
        Assert.NotEmpty(document.PlainText);

        using var incorrect = new MemoryStream(bytes);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            DocumentFormats.Docx.LoadWithPasswordAsync(incorrect, "incorrect password"));
    }

    [Fact]
    public async Task Corrupt_standard_ciphertext_does_not_return_a_document()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Interchange", "libreoffice-encrypted.docx");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[1544] ^= 0x80; // First EncryptedPackage ciphertext byte in this 512-byte-sector fixture.
        Assert.True(OfficeEncryptedPackage.IsEncrypted(bytes));
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<FormatException>(() =>
            DocumentFormats.Docx.LoadWithPasswordAsync(stream, "Textalonia fixture password"));
    }

    [Fact]
    public async Task Standard_package_corruption_after_zip_header_fails_validation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Interchange", "libreoffice-encrypted.docx");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[1600] ^= 0x40; // Ciphertext beyond the first ZIP block in this fixture.
        using var stream = new MemoryStream(bytes);
        var error = await Record.ExceptionAsync(() =>
            DocumentFormats.Docx.LoadWithPasswordAsync(stream, "Textalonia fixture password"));
        Assert.True(error is FormatException or InvalidDataException, error?.ToString());
    }

    [Fact]
    public async Task Password_load_rejects_an_unencrypted_docx()
    {
        using var stream = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(Textalonia.Model.FlowDocument.FromText("plain"), stream);
        stream.Position = 0;
        await Assert.ThrowsAsync<FormatException>(() =>
            DocumentFormats.Docx.LoadWithPasswordAsync(stream, "unused"));
    }

    [Fact]
    public void Finds_both_encryption_streams_in_the_compound_root()
    {
        var data = Compound("EncryptionInfo", "EncryptedPackage");

        Assert.True(OfficeEncryptedPackage.IsCompoundFile(data));
        Assert.True(OfficeEncryptedPackage.IsEncrypted(data));
    }

    [Fact]
    public void Legacy_doc_and_magic_bytes_are_not_misidentified_as_encrypted_open_xml()
    {
        var legacy = Compound("WordDocument", "1Table");
        Assert.True(OfficeEncryptedPackage.IsCompoundFile(legacy));
        Assert.False(OfficeEncryptedPackage.IsEncrypted(legacy));

        var justSignature = new byte[1536];
        Array.Copy(legacy, justSignature, 8);
        Assert.True(OfficeEncryptedPackage.IsCompoundFile(justSignature));
        Assert.False(OfficeEncryptedPackage.IsEncrypted(justSignature));
    }

    [Fact]
    public void Rejects_forged_directory_cycle_and_fat_sector_pointer()
    {
        var cycle = Compound("EncryptionInfo", "EncryptedPackage");
        Put32(cycle, 1024 + 128 + 68, 2); // entry 1's left sibling loops back from entry 2
        Assert.False(OfficeEncryptedPackage.IsEncrypted(cycle));

        var badFat = Compound("EncryptionInfo", "EncryptedPackage");
        Put32(badFat, 76, 9000);
        Assert.False(OfficeEncryptedPackage.IsEncrypted(badFat));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(8192u)]
    public void Rejects_nonstandard_mini_stream_cutoff(uint cutoff)
    {
        var data = Compound("EncryptionInfo", "EncryptedPackage");
        Put32(data, 56, cutoff);
        Assert.False(OfficeEncryptedPackage.IsEncrypted(data));
    }

    private static byte[] Compound(string first, string second)
    {
        // Three-sector CFB v3: header, FAT (sector 0), root directory (sector 1).
        var data = new byte[1536];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(data, 0);
        Put16(data, 24, 0x003e);
        Put16(data, 26, 3);
        Put16(data, 28, 0xfffe);
        Put16(data, 30, 9);
        Put16(data, 32, 6);
        Put32(data, 44, 1);             // FAT sector count
        Put32(data, 48, 1);             // first directory sector
        Put32(data, 56, 4096);          // mini stream cutoff
        Put32(data, 60, 0xfffffffe);    // no mini FAT
        Put32(data, 68, 0xfffffffe);    // no DIFAT chain
        Put32(data, 76, 0);             // DIFAT[0] = FAT sector
        for (var i = 1; i < 109; i++) Put32(data, 76 + i * 4, 0xffffffff);
        Put32(data, 512, 0xfffffffd);   // FAT sector marker
        Put32(data, 516, 0xfffffffe);   // directory sector ends chain
        for (var i = 2; i < 128; i++) Put32(data, 512 + i * 4, 0xffffffff);
        Entry(data, 1024, "Root Entry", 5, 0xffffffff, 0xffffffff, 1);
        Entry(data, 1152, first, 2, 0xffffffff, 2, 0xffffffff);
        Entry(data, 1280, second, 2, 0xffffffff, 0xffffffff, 0xffffffff);
        return data;
    }

    private static void Entry(byte[] data, int offset, string name, byte type, uint left, uint right, uint child)
    {
        Encoding.Unicode.GetBytes(name + '\0').CopyTo(data, offset);
        Put16(data, offset + 64, (ushort)((name.Length + 1) * 2));
        data[offset + 66] = type;
        Put32(data, offset + 68, left);
        Put32(data, offset + 72, right);
        Put32(data, offset + 76, child);
        Put32(data, offset + 116, 0xfffffffe);
    }

    private static void Put16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    private static void Put32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
}
