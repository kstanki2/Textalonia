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

/// <summary>A callback notification for one selected recipient.</summary>
public sealed record MailMergeRecordEvent(int RecordIndex, bool IsPreview);

/// <summary>A callback notification for one repeated child record.</summary>
public sealed record MailMergeRegionEvent(string Name, int ItemIndex, int Depth, bool IsStarting);

/// <summary>A diagnostic associated with a particular recipient.</summary>
public sealed record MailMergeDiagnostic(int RecordIndex, string Code, string Message, Guid? FieldId = null);

/// <summary>Deterministic options shared by preview and document generation.</summary>
public sealed record MailMergeOptions
{
    /// <summary>Explicitly evaluates general fields using each record. Null preserves their cached results.</summary>
    public Textalonia.Model.Fields.FieldEvaluationOptions? FieldOptions { get; init; }
    /// <summary>Receives diagnostics from general field evaluation. Callback exceptions abort the merge.</summary>
    public Action<Textalonia.Model.Fields.FieldDiagnostic>? FieldDiagnostic { get; init; }
    /// <summary>Called before a recipient is processed. Throwing aborts enumeration.</summary>
    public Action<MailMergeRecordEvent>? RecordStarting { get; init; }
    /// <summary>Called after a recipient is processed. Throwing aborts enumeration.</summary>
    public Action<MailMergeRecordEvent>? RecordCompleted { get; init; }
    /// <summary>Called before and after each repeated child record.</summary>
    public Action<MailMergeRegionEvent>? RegionProgress { get; init; }
    /// <summary>Receives field and record errors tagged with a recipient index.</summary>
    public Action<MailMergeDiagnostic>? RecordDiagnostic { get; init; }

    /// <summary>Formatting culture; invariant by default. A read-only copy is captured when processing starts.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    /// <summary>Missing values use a field's fallback before applying this policy.</summary>
    public MissingFieldBehavior MissingFieldBehavior { get; init; } = MissingFieldBehavior.Throw;
}
