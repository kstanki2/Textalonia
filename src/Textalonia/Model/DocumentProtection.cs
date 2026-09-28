using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace Textalonia.Model;

public enum DocumentProtectionMode { None, ReadOnly, FormsOnly }

/// <summary>Editing restrictions, independent of encrypted file storage and host authentication.</summary>
public sealed record DocumentProtection
{
    public DocumentProtectionMode Mode { get; init; }
    public bool Enforce { get; init; } = true;
    /// <summary>Empty protects the whole document; otherwise only these physical sections are protected.</summary>
    public ImmutableArray<Guid> ProtectedSectionIds { get; init; } = [];
    public DocumentProtectionPassword? Password { get; init; }
}

/// <summary>A salted verifier. The original password is never retained in a snapshot.</summary>
public sealed record DocumentProtectionPassword
{
    public string Algorithm { get; init; } = "Office-SHA512";
    public string Salt { get; init; } = "";
    public string Hash { get; init; } = "";
    public int Iterations { get; init; } = 100_000;

    public static DocumentProtectionPassword Create(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        return new() { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(
            OfficeHash(password, salt, 100_000, HashAlgorithmName.SHA512)) };
    }

    public bool Verify(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        Validate();
        var expected = Convert.FromBase64String(Hash);
        var salt = Convert.FromBase64String(Salt);
        byte[] actual;
        if (Algorithm == "PBKDF2-SHA256") actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
        else if (Algorithm is "Office-SHA1" or "Office-SHA256" or "Office-SHA384" or "Office-SHA512")
            actual = OfficeHash(password, salt, Iterations, new HashAlgorithmName(Algorithm[7..]));
        else return false;
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // ISO write-protection hashing: salt + UTF-16LE password, then hash + LE32 counter.
    // https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-offcrypto/1357ea58-646e-4483-92ef-95d718079d6f
    private static byte[] OfficeHash(string password, byte[] salt, int iterations, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(salt); hash.AppendData(Encoding.Unicode.GetBytes(password));
        var result = hash.GetHashAndReset();
        Span<byte> counter = stackalloc byte[4];
        for (var i = 0; i < iterations; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(counter, i);
            hash.AppendData(result); hash.AppendData(counter); hash.GetHashAndReset(result);
        }
        return result;
    }

    internal void Validate()
    {
        if (Algorithm is null || Algorithm.Length > 64 || Salt is null || Salt.Length > 256 || Hash is null || Hash.Length > 256 || Iterations is < 0 or > 1_000_000)
            throw new FormatException("Invalid protection password verifier.");
        var salt = Convert.FromBase64String(Salt); var hash = Convert.FromBase64String(Hash);
        if (salt.Length is < 1 or > 128 || hash.Length is < 2 or > 128 || (Algorithm == "PBKDF2-SHA256" && (hash.Length != 32 || Iterations == 0) || Algorithm == "Office-SHA1" && hash.Length != 20 || Algorithm == "Office-SHA256" && hash.Length != 32 || Algorithm == "Office-SHA384" && hash.Length != 48 || Algorithm == "Office-SHA512" && hash.Length != 64))
            throw new FormatException("Invalid protection password verifier.");
    }
}

/// <summary>A read-only range or a user/group exception to document protection. Identities are supplied by the host.</summary>
public sealed record DocumentPermissionRange
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DocumentAnchor Start { get; init; }
    public required DocumentAnchor End { get; init; }
    public bool IsReadOnly { get; init; }
    public string? User { get; init; }
    public string? Group { get; init; }
}

internal static class DocumentProtectionValidation
{
    internal static void Validate(FlowDocument document)
    {
        if (document.Protection is not { } protection || !Enum.IsDefined(protection.Mode) || protection.ProtectedSectionIds.IsDefault ||
            protection.ProtectedSectionIds.Length > 10000 || protection.ProtectedSectionIds.Distinct().Count() != protection.ProtectedSectionIds.Length ||
            protection.ProtectedSectionIds.Any(id => !document.Sections.Any(s => s.Id == id)) || document.PermissionRanges.IsDefault || document.PermissionRanges.Length > 10000)
            throw new FormatException("Invalid document protection.");
        protection.Password?.Validate();
        var ids = new HashSet<Guid>();
        foreach (var range in document.PermissionRanges)
            if (range is null || range.Id == Guid.Empty || !ids.Add(range.Id) || range.Start is null || range.End is null ||
                range.Start.StoryId != range.End.StoryId || range.Start.Resolve(document) > range.End.Resolve(document) ||
                range.User is not null && !InlineDescriptor.ValidKey(range.User) || range.Group is not null && !InlineDescriptor.ValidKey(range.Group))
                throw new FormatException("Invalid permission range.");
    }
}
