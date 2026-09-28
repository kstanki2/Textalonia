using System.Collections.Immutable;
using System.Text;

namespace Textalonia.Model.Fields;

public sealed record FieldProjectionSpan(int DocumentStart, int DocumentLength, int DisplayStart, int DisplayLength, Guid? FieldId);

/// <summary>A read-only code view. Storage, editing, search and clipboard coordinates remain cached-result UTF-16 positions.</summary>
public sealed class FieldCodeProjection
{
    public string Text { get; }
    public ImmutableArray<FieldProjectionSpan> Spans { get; }
    public int DocumentLength { get; }
    private FieldCodeProjection(string text, ImmutableArray<FieldProjectionSpan> spans, int documentLength)
    { Text = text; Spans = spans; DocumentLength = documentLength; }
    public static FieldCodeProjection Create(FlowDocument document, Guid storyId = default, bool showAll = false)
    {
        var index = document.GetStoryIndex(storyId); var text = new StringBuilder();
        var spans = ImmutableArray.CreateBuilder<FieldProjectionSpan>(); var offset = 0;
        void Literal(int end)
        {
            if (end <= offset) return;
            spans.Add(new(offset, end - offset, text.Length, end - offset, null));
            text.Append(index.ReadText(offset, end - offset)); offset = end;
        }
        foreach (var field in document.Fields.Where(f => f.Start.StoryId == storyId && (showAll || f.ShowCode))
            .OrderBy(f => f.Start.Resolve(document)).ThenByDescending(f => f.End.Resolve(document)))
        {
            var start = field.Start.Resolve(document); var end = field.End.Resolve(document);
            if (start < offset) continue;
            Literal(start); var code = "{ " + field.Instruction.Trim() + " }";
            spans.Add(new(start, end - start, text.Length, code.Length, field.Id)); text.Append(code); offset = end;
        }
        Literal(index.Length);
        return new(text.ToString(), spans.ToImmutable(), index.Length);
    }
    public int ToDocumentOffset(int displayOffset, AnchorAffinity affinity = AnchorAffinity.After)
    {
        if (displayOffset < 0 || displayOffset > Text.Length) throw new ArgumentOutOfRangeException(nameof(displayOffset));
        foreach (var span in Spans)
            if (displayOffset >= span.DisplayStart && displayOffset < span.DisplayStart + span.DisplayLength)
                return span.FieldId is null ? span.DocumentStart + displayOffset - span.DisplayStart :
                    displayOffset == span.DisplayStart || affinity == AnchorAffinity.Before ? span.DocumentStart : span.DocumentStart + span.DocumentLength;
        return DocumentLength;
    }
    public int ToDisplayOffset(int documentOffset, AnchorAffinity affinity = AnchorAffinity.After)
    {
        if (documentOffset < 0 || documentOffset > DocumentLength) throw new ArgumentOutOfRangeException(nameof(documentOffset));
        foreach (var span in Spans)
            if (documentOffset >= span.DocumentStart && documentOffset < span.DocumentStart + span.DocumentLength ||
                span.DocumentLength == 0 && documentOffset == span.DocumentStart)
                return span.FieldId is null ? span.DisplayStart + documentOffset - span.DocumentStart :
                    documentOffset == span.DocumentStart && affinity == AnchorAffinity.Before ? span.DisplayStart : span.DisplayStart + span.DisplayLength;
        return Text.Length;
    }
}
