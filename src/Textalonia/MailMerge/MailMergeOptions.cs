using System.Globalization;

namespace Textalonia.MailMerge;

/// <summary>What to do when a record has no value for a field and the field has no fallback.</summary>
public enum MissingFieldBehavior
{
    /// <summary>Reject the record with a <see cref="KeyNotFoundException"/>.</summary>
    Throw,
    /// <summary>Keep the live field and its current display text.</summary>
    KeepField,
    /// <summary>Use empty text.</summary>
    Empty
}

/// <summary>Deterministic options shared by preview and document generation.</summary>
public sealed record MailMergeOptions
{
    /// <summary>Formatting culture; invariant by default. A read-only copy is captured when processing starts.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    /// <summary>Missing values use a field's fallback before applying this policy.</summary>
    public MissingFieldBehavior MissingFieldBehavior { get; init; } = MissingFieldBehavior.Throw;
}
