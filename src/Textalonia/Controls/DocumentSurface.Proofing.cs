using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Textalonia.Proofing;

namespace Textalonia.Controls;

public partial class DocumentSurface
{
    private static readonly Pen SpellingPen = new(Brushes.Firebrick, 1.2);
    private bool _hasProofingContextClick;
    private int? _proofingContextPosition;
    private int _proofingContextRevision;
    private Guid _proofingContextStory;
    private TextaloniaEditor? _proofingContextEditor;
    private long _proofingContextTick;

    private void CaptureProofingContextClick(PointerPressedEventArgs e)
    {
        if (Editor is not { } editor || !e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        _hasProofingContextClick = true;
        _proofingContextEditor = editor;
        _proofingContextTick = Stopwatch.GetTimestamp();
        _proofingContextRevision = editor.Session.Revision;
        _proofingContextStory = editor.ActiveStoryId;
        EnsureLayout(Bounds.Width);
        _proofingContextPosition = TryHitTest(e.GetPosition(this), out var position) ? position : null;
    }

    private int? TakeProofingContextPosition(TextaloniaEditor editor)
    {
        if (!_hasProofingContextClick) return editor.Session.Selection.Active;
        _hasProofingContextClick = false;
        return ReferenceEquals(_proofingContextEditor, editor) &&
            Stopwatch.GetElapsedTime(_proofingContextTick) <= TimeSpan.FromSeconds(2) &&
            _proofingContextRevision == editor.Session.Revision && _proofingContextStory == editor.ActiveStoryId
            ? _proofingContextPosition : null;
    }

    internal void RefreshProofingContextMenu()
    {
        if (Editor is not { } editor || ContextMenu is not { IsOpen: false } menu || _hasProofingContextClick) return;
        menu.ItemsSource = BuildEditorContextItems(editor, editor.Session.Selection.Active);
    }

    private void DrawProofingUnderlines(DrawingContext context, Rect viewport)
    {
        if (Editor is not { SpellingDiagnostics.Count: > 0 } editor || HasComposition) return;
        var visible = GeometryRanges().Where(range => range.Bounds.Intersects(viewport)).ToArray();
        if (visible.Length == 0) return;
        foreach (var diagnostic in editor.SpellingDiagnostics)
        {
            if (!visible.Any(range => diagnostic.Start < range.End && diagnostic.End > range.Start)) continue;
            foreach (var selection in GeometrySelectionRects(diagnostic.Start, diagnostic.Length))
            {
                var clipped = selection.Intersect(viewport);
                if (clipped.Width <= 0 || clipped.Height <= 0) continue;
                var rect = ToDocument(clipped);
                var y = rect.Bottom - Math.Min(2, rect.Height / 5);
                for (var x = rect.Left; x < rect.Right; x += 4)
                {
                    var middle = Math.Min(rect.Right, x + 2);
                    var end = Math.Min(rect.Right, x + 4);
                    context.DrawLine(SpellingPen, new Point(x, y), new Point(middle, y + 1.5));
                    context.DrawLine(SpellingPen, new Point(middle, y + 1.5), new Point(end, y));
                }
            }
        }
    }

    private ContextMenu CreateEditorContextMenu(TextaloniaEditor editor)
    {
        var menu = new ContextMenu();
        menu.ItemsSource = BuildEditorContextItems(editor, editor.Session.Selection.Active);
        menu.Opening += (_, _) => menu.ItemsSource = BuildEditorContextItems(editor, TakeProofingContextPosition(editor));
        menu.Closed += (_, _) =>
        { _hasProofingContextClick = false; RefreshProofingContextMenu(); };
        return menu;
    }

    private static object[] BuildEditorContextItems(TextaloniaEditor editor, int? position)
    {
        var items = new List<object>();
        var diagnostic = position is { } offset ? editor.SpellingDiagnosticAt(offset) : null;
        if (diagnostic is not null)
        {
            foreach (var suggestion in diagnostic.Suggestions.Take(5))
            {
                var candidate = suggestion;
                var issue = diagnostic;
                var item = new MenuItem { Header = candidate };
                item.Click += (_, _) => editor.ReplaceSpelling(issue, candidate);
                items.Add(item);
            }
            var ignore = new MenuItem { Header = $"Ignore all: {diagnostic.Word}" };
            ignore.Click += (_, _) => editor.IgnoreSpelling(diagnostic);
            items.Add(ignore);
            if (editor.SpellChecker is IWritableSpellChecker)
            {
                var add = new MenuItem { Header = "Add to dictionary" };
                add.Click += async (_, _) =>
                {
                    try { await editor.AddSpellingToDictionaryAsync(diagnostic); }
                    catch (Exception error) { editor.ReportError(error); }
                };
                items.Add(add);
            }
            items.Add(new Separator());
        }
        if (editor.SpellingDiagnostics.Count > 0)
        {
            var next = new MenuItem { Header = "Next spelling error" };
            next.Click += (_, _) => editor.SelectNextSpellingError();
            items.Add(next);
            items.Add(new Separator());
        }
        items.Add(new MenuItem { Header = "Undo", Command = editor.UndoCommand });
        items.Add(new MenuItem { Header = "Redo", Command = editor.RedoCommand });
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "Cut", Command = editor.CutCommand });
        items.Add(new MenuItem { Header = "Copy", Command = editor.CopyCommand });
        items.Add(new MenuItem { Header = "Paste", Command = editor.PasteCommand });
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "Select all", Command = editor.SelectAllCommand });
        return items.ToArray();
    }
}
