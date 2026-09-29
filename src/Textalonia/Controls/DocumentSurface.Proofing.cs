using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Textalonia.Editing;
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
            AddCommand(EditorCommandId.Proofing);
            items.Add(new Separator());
        }
        AddCommand(EditorCommandId.Undo);
        AddCommand(EditorCommandId.Redo);
        items.Add(new Separator());
        AddCommand(EditorCommandId.Cut);
        AddCommand(EditorCommandId.Copy);
        AddCommand(EditorCommandId.Paste);
        var pasteSpecialCommand = editor.Commands[EditorCommandId.PasteSpecial];
        if (pasteSpecialCommand.IsVisible)
        {
            var pasteSpecial = new MenuItem { Header = pasteSpecialCommand.DisplayText, IsEnabled = pasteSpecialCommand.Capability == CommandCapability.Enabled };
            pasteSpecial.ItemsSource = new object[]
            {
                PasteChoice("Textalonia.UI.PasteSpecial.NativeFragment", "Textalonia fragment", PasteSpecialFormat.NativeFragment),
                PasteChoice("Textalonia.UI.PasteSpecial.Html", "HTML", PasteSpecialFormat.Html),
                PasteChoice("Textalonia.UI.PasteSpecial.PlainText", "Plain text", PasteSpecialFormat.PlainText)
            };
            items.Add(pasteSpecial);
        }
        items.Add(new Separator());
        AddCommand(EditorCommandId.InsertSymbol);
        AddCommand(EditorCommandId.DocumentProperties);
        if (editor.Session.CurrentCell() is not null)
            AddCommand(EditorCommandId.TableProperties);
        if (editor.CurrentImageOrOle is not null)
            AddCommand(EditorCommandId.PictureProperties);
        items.Add(new Separator());
        AddCommand(EditorCommandId.SelectAll);
        return items.ToArray();

        void AddCommand(EditorCommandId id)
        {
            var command = editor.Commands[id];
            if (command.IsVisible) items.Add(new MenuItem { Header = command.DisplayText, Command = command });
        }

        MenuItem PasteChoice(string key, string fallback, PasteSpecialFormat format)
        {
            return new MenuItem { Header = editor.Commands.Localize?.Invoke(key) is { Length: > 0 } localized ? localized : fallback,
                Command = pasteSpecialCommand, CommandParameter = format };
        }
    }
}
