namespace Textalonia.Controls;

/// <summary>An exact text layout needs more UTF-16 context than the host permits.</summary>
public sealed class ShapingLimitExceededException(Guid paragraphId, int characterLimit, int requestedCharacters)
    : InvalidOperationException($"Paragraph {paragraphId} requires a shaping input of {requestedCharacters} UTF-16 units; the configured limit is {characterLimit}.")
{
    public Guid ParagraphId { get; } = paragraphId;
    public int CharacterLimit { get; } = characterLimit;
    /// <summary>The next requested input size, not an estimate of native memory.</summary>
    public int RequestedCharacters { get; } = requestedCharacters;
}
