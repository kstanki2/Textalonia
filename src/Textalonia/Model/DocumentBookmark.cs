namespace Textalonia.Model;

/// <summary>A named range, including a collapsed insertion boundary, in one document story.</summary>
public sealed record DocumentBookmark
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DocumentAnchor Start { get; init; } = new() { Affinity = AnchorAffinity.Before };
    public DocumentAnchor End { get; init; } = new();
}

public enum InternalLinkActivation { ModifierClick, Click }

/// <summary>An internal destination is independent of the external URI allowlist.</summary>
public sealed record InternalLinkDestination
{
    public string BookmarkName { get; init; } = "";
    public string? Tooltip { get; init; }
    public InternalLinkActivation Activation { get; init; }
}
