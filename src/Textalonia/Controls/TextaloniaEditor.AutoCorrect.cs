using System.Globalization;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Proofing;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>Committed-input correction service. Set null to disable AutoCorrect.</summary>
    public IAutoCorrectService? AutoCorrectService { get; set; } = new AutoCorrectService();

    /// <summary>Characters that complete a token. Hosts can add or remove boundaries.</summary>
    public ISet<char> AutoCorrectBoundaries { get; } =
        new HashSet<char>([' ', '\t', '\n', '.', ',', ';', ':', '!', '?']);

    internal void HandleCommittedText(string text, int revisionBefore)
    {
        if (Session.Revision == revisionBefore || string.IsNullOrEmpty(text)) return;
        var inserted = FlowDocument.NormalizeNewlines(text).Replace("\0", "");
        var end = Session.Selection.Active;
        var start = end - inserted.Length;
        if (start < 0 || Session.Index.ReadText(start, inserted.Length) != inserted) return;
        // Work from right to left: changing a later token cannot shift an earlier boundary.
        for (var i = inserted.Length - 1; i >= 0; i--)
            if (AutoCorrectBoundaries.Contains(inserted[i]))
                TryAutoCorrectAtBoundary(inserted[i], start + i);
    }

    internal void HandleCommittedBoundary(char boundary, int revisionBefore)
    {
        if (Session.Revision == revisionBefore || !AutoCorrectBoundaries.Contains(boundary)) return;
        var at = Session.Selection.Active - 1;
        if (at < 0) return;
        var inserted = Session.Index.CharAt(at);
        if (inserted != boundary && !(boundary == '\n' && inserted == '\u2028')) return;
        TryAutoCorrectAtBoundary(boundary, at);
    }

    private bool TryAutoCorrectAtBoundary(char boundary, int boundaryPosition)
    {
        if (AutoCorrectService is null || !AutoCorrectBoundaries.Contains(boundary) ||
            !Session.Selection.IsEmpty || Session.GetCapability(EditOperation.Text) != CommandCapability.Enabled ||
            boundaryPosition <= 0 || boundaryPosition >= Session.Index.Length)
            return false;
        var end = boundaryPosition;
        var position = Session.Index.At(end - 1);
        var start = end;
        while (start > position.Start && !char.IsWhiteSpace(Session.Index.CharAt(start - 1))) start--;
        var candidate = Session.Index.ReadText(start, end - start);
        var looksLikeUrl = candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeUrl)
            for (var i = end - 1; i >= start; i--)
                if (!char.IsWhiteSpace(Session.Index.CharAt(i)) && AutoCorrectBoundaries.Contains(Session.Index.CharAt(i)))
                { start = i + 1; break; }
        while (start < end && "([{\"'".Contains(Session.Index.CharAt(start))) start++;
        while (end > start && ".,!?;:)]}\"'".Contains(Session.Index.CharAt(end - 1))) end--;
        if (end <= start) return false;
        var value = Session.Index.ReadText(start, end - start);
        if (value.IndexOf('\uFFFC') >= 0 || value.IndexOf('\n') >= 0) return false;
        var local = start - position.Start;
        var resolver = new DocumentStyleResolver(Session.ActiveDocument);
        var runs = position.Paragraph.Slice(local, end - start);
        if (runs.IsEmpty) return false;
        var style = resolver.ResolveText(position.Paragraph, runs[0].Style);
        // A correction must not erase direct formatting or cross a NoProof/language boundary.
        if (style.NoProof || runs.Any(run => run.Inline is not null ||
            resolver.ResolveText(position.Paragraph, run.Style) != style)) return false;
        CultureInfo culture;
        try { culture = style.Language is { Length: > 0 } name ? CultureInfo.GetCultureInfo(name) : CultureInfo.CurrentCulture; }
        catch (CultureNotFoundException) { culture = CultureInfo.CurrentCulture; }
        var context = new AutoCorrectContext(value, culture, Session.ActiveStoryId, start, end, boundary, style);
        try
        {
            var replacement = AutoCorrectService.GetReplacement(context);
            return replacement is not null && Session.TryApplyAutoCorrect(start, end, replacement);
        }
        catch (Exception error)
        {
            ReportError(error);
            return false;
        }
    }
}
