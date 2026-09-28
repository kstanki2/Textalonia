using System.Collections.Immutable;

namespace Textalonia.Editing;

public enum EditOperation { Text, Formatting, Tables, InlineObjects, Forms, Structure, Metadata, Clipboard, Undo, Redo }
public enum CommandCapability { Enabled, Disabled, Hidden }

/// <summary>Host-owned capabilities. Hidden and disabled operations are both rejected by the session.</summary>
public sealed record EditPolicy
{
    public ImmutableDictionary<EditOperation, CommandCapability> Commands { get; init; } = ImmutableDictionary<EditOperation, CommandCapability>.Empty;
    public CommandCapability GetCapability(EditOperation operation) => Commands.GetValueOrDefault(operation, CommandCapability.Enabled);
}

/// <summary>Host-resolved identity; the editor performs no authentication.</summary>
public sealed record EditIdentity
{
    public string? User { get; init; }
    public ImmutableHashSet<string> Groups { get; init; } = ImmutableHashSet<string>.Empty;
}
