using Avalonia;
using Avalonia.Input;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Standard navigation, editing and clipboard shortcuts. Override KeyDown and call base for fall-through.</summary>
public class DefaultKeyboardComponent : DocumentInputComponent, IKeyboardComponent
{
    public virtual void KeyDown(KeyEventArgs e)
    {
        if (e.Handled || Context.Editor is null) return;
        var session = Context.Editor.Session;
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var command = e.KeyModifiers.HasFlag(primary) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var word = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Alt) : command;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        void Move(int position)
        {
            Context.CancelComposition();
            Context.Surface.SelectVisualCaret(VisualCaret.Logical(position), shift);
        }
        if (shift && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && session.CurrentCell() is not null &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            Context.CancelComposition();
            Context.Editor.ExtendTableCellSelection(e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0,
                e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0);
            Context.PreferredCaretX = null;
            e.Handled = true;
            return;
        }
        if (command)
        {
            System.Windows.Input.ICommand? action = e.Key switch
            {
                Key.B => Context.Editor.BoldCommand, Key.I => Context.Editor.ItalicCommand, Key.U => Context.Editor.UnderlineCommand,
                Key.Z => shift ? Context.Editor.RedoCommand : Context.Editor.UndoCommand,
                Key.Y => Context.Editor.RedoCommand, Key.C => Context.Editor.CopyCommand, Key.X => Context.Editor.CutCommand,
                Key.V => Context.Editor.PasteCommand, Key.A => Context.Editor.SelectAllCommand, _ => null
            };
            if (action is not null) { Context.CancelComposition(); if (action.CanExecute(null)) action.Execute(null); e.Handled = true; return; }
            if (e.Key == Key.F) { Context.Editor.RequestFind(); e.Handled = true; return; }
        }
        Context.Surface.EnsureLayout(Context.Surface.Bounds.Width);
        try
        {
            switch (e.Key)
            {
                case Key.Left:
                    Context.CancelComposition();
                    Context.Surface.MoveVisualCaret(false, shift, word);
                    Context.PreferredCaretX = null; break;
                case Key.Right:
                    Context.CancelComposition();
                    Context.Surface.MoveVisualCaret(true, shift, word);
                    Context.PreferredCaretX = null; break;
                case Key.Up:
                case Key.Down:
                case Key.PageUp:
                case Key.PageDown:
                    var caret = Context.CaretRectangle;
                    if (Context.Editor.LayoutError is not null) return;
                    Context.PreferredCaretX ??= caret.X;
                    var direction = e.Key is Key.Up or Key.PageUp ? -1 : 1;
                    var distance = e.Key is Key.PageUp or Key.PageDown ? Math.Max(40, Context.ViewportHeight) : caret.Height;
                    Context.CancelComposition();
                    Context.Surface.MoveVisualCaretToPoint(new Point(Context.PreferredCaretX.Value, caret.Y + caret.Height / 2 + direction * distance), shift);
                    break;
                case Key.Home:
                case Key.End:
                    if (command) Move(e.Key == Key.Home ? 0 : session.Index.Length);
                    else
                    {
                        if (Context.Editor.LayoutError is not null) return;
                        Context.CancelComposition();
                        Context.Surface.MoveVisualLineBoundary(e.Key == Key.End, shift);
                    }
                    Context.PreferredCaretX = null; break;
                case Key.Back: Context.CancelComposition(); if (Context.Editor.CellSelection is not null) Context.Editor.ClearSelectedTableCellContents(); else session.DeleteBackward(word); Context.PreferredCaretX = null; break;
                case Key.Delete: Context.CancelComposition(); if (Context.Editor.CellSelection is not null) Context.Editor.ClearSelectedTableCellContents(); else session.DeleteForward(word); Context.PreferredCaretX = null; break;
                case Key.Enter: Context.CancelComposition(); if (shift) session.InsertText("\u2028"); else session.InsertParagraph(); Context.PreferredCaretX = null; break;
                case Key.Tab:
                    if (session.CurrentCell() is { } cell)
                    {
                        var cellIds = cell.Table.Rows.SelectMany(r => r).Select(c => c.Id).ToHashSet();
                        var cells = session.Index.Paragraphs.Where(p => cellIds.Contains(p.ContainerId)).GroupBy(p => p.ContainerId).ToArray();
                        var current = Array.FindIndex(cells, g => g.Key == cell.Table.Rows[cell.Row][cell.Column].Id);
                        var next = current + (shift ? -1 : 1);
                        if (next >= 0 && next < cells.Length) Move(cells[next].First().Start);
                        else if (!shift && !Context.Editor.IsReadOnly && cell.Table.Rows.SelectMany(r => r).All(c => c.RowSpan == 1 && c.ColumnSpan == 1))
                        {
                            var rowIndex = cell.Table.Rows.Length;
                            session.UpdateCurrentTable((table, _, _) => table.InsertRow(rowIndex));
                            var table = session.CurrentCell()?.Table;
                            if (table is not null)
                            {
                                var target = session.Index.Paragraphs.First(p => p.ContainerId == table.Rows[rowIndex][0].Id);
                                session.Select(target.Start, target.Start);
                            }
                        }
                        else return;
                    }
                    else if (Context.Editor.AcceptsTab) session.InsertText("\t");
                    else return;
                    break;
                case Key.Escape:
                    Context.Editor.CancelTableResize(); Context.Editor.ClearTableCellSelection();
                    if (Context.IsComposing) Context.CancelComposition();
                    else session.Select(session.Selection.Active, session.Selection.Active);
                    break;
                default: return;
            }
        }
        catch (ShapingLimitExceededException error) { Context.Surface.RejectLayout(error); }
        e.Handled = true;
    }

}
