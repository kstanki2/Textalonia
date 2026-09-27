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
    /// <summary>Explicitly evaluates general fields using each record. Null preserves their cached results.</summary>
    public Textalonia.Model.Fields.FieldEvaluationOptions? FieldOptions { get; init; }
    /// <summary>Receives diagnostics from general field evaluation. Callback exceptions abort the merge.</summary>
    public Action<Textalonia.Model.Fields.FieldDiagnostic>? FieldDiagnostic { get; init; }

    /// <summary>Formatting culture; invariant by default. A read-only copy is captured when processing starts.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    /// <summary>Missing values use a field's fallback before applying this policy.</summary>
    public MissingFieldBehavior MissingFieldBehavior { get; init; } = MissingFieldBehavior.Throw;
}
