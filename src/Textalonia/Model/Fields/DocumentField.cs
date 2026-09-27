namespace Textalonia.Model;

/// <summary>A field's cached result is ordinary rich content between two story anchors.
/// Instructions are metadata and do not consume document coordinates. ShowCode is a display preference.</summary>
public sealed record DocumentField
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Instruction { get; init; } = "";
    public DocumentAnchor Start { get; init; } = new();
    public DocumentAnchor End { get; init; } = new();
    public bool IsLocked { get; init; }
    public bool IsDirty { get; init; } = true;
    public bool ShowCode { get; init; }
    /// <summary>Retains the native format and fallback when adapting the atomic MERGEFIELD convenience API.</summary>
    public MergeFieldInlinePayload? LegacyMergeField { get; init; }
}
