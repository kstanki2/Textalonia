using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    private Guid _observedStoryId;
    private Guid? _headerFooterSection;
    private bool _editingFooter;
    private HeaderFooterVariant _headerFooterVariant;
    internal int ActiveStoryPageIndex { get; set; } = -1;

    /// <summary>The active editing story; Guid.Empty identifies the main body. Selection offsets belong to this story.</summary>
    public Guid ActiveStoryId => Session.ActiveStoryId;
    public event EventHandler? ActiveStoryChanged;

    /// <summary>Activates a header or footer. Linked content remains shared until explicitly unlinked.</summary>
    public void EditHeaderFooter(bool footer, HeaderFooterVariant variant = HeaderFooterVariant.Primary, Guid? sectionId = null)
    {
        _surface?.Composition.Cancel();
        var id = sectionId ?? Session.CurrentSection?.Id ?? Guid.Empty;
        _headerFooterSection = id;
        _editingFooter = footer; _headerFooterVariant = variant;
        ViewMode = DocumentViewMode.PrintLayout;
        ActiveStoryPageIndex = Math.Max(0, CurrentPageNumber - 1);
        Session.ActivateHeaderFooter(id, footer, variant);
        _headerFooterSection = sectionId ?? Session.CurrentSection?.Id ?? Session.Document.Sections.FirstOrDefault()?.Id;
        FocusDocument();
    }

    public void EditHeader() => EditHeaderFooter(false);
    public void EditFooter() => EditHeaderFooter(true);

    /// <summary>Returns to the saved main-body selection without creating an undo entry.</summary>
    public void CloseStory()
    {
        _surface?.Composition.Cancel();
        Session.ReturnToBody(); ActiveStoryPageIndex = -1; _headerFooterSection = null;
        FocusDocument();
    }

    public void InsertFootnote(string? customMark = null) => InsertNote(DocumentNoteKind.Footnote, customMark);
    public void InsertEndnote(string? customMark = null) => InsertNote(DocumentNoteKind.Endnote, customMark);

    private void InsertNote(DocumentNoteKind kind, string? customMark)
    {
        if (Session.IsReadOnly) return;
        _surface?.Composition.Cancel();
        ViewMode = DocumentViewMode.PrintLayout;
        ActiveStoryPageIndex = -1;
        Session.InsertNote(kind, customMark);
        FocusDocument();
    }

    public void EditNote(Guid noteId)
    {
        _surface?.Composition.Cancel(); ViewMode = DocumentViewMode.PrintLayout;
        ActiveStoryPageIndex = -1; Session.ActivateNote(noteId); FocusDocument();
    }

    public void InsertPageField(PageFieldKind field) => Session.InsertInline(InlineDescriptor.PageField(field));

    public void LinkHeaderFooterToPrevious(bool linked)
    {
        if (Session.IsReadOnly || _headerFooterSection is not { } sectionId ||
            !Session.Document.Stories.TryGetValue(ActiveStoryId, out var active) ||
            active.Kind is not (DocumentStoryKind.Header or DocumentStoryKind.Footer)) return;
        Session.SetHeaderFooterLink(sectionId, _editingFooter, _headerFooterVariant, linked);
        Session.ActivateHeaderFooter(sectionId, _editingFooter, _headerFooterVariant);
    }

    internal void SetHeaderFooterInstance(Guid sectionId, bool footer, HeaderFooterVariant variant, int pageIndex)
    {
        _headerFooterSection = sectionId; _editingFooter = footer; _headerFooterVariant = variant;
        ActiveStoryPageIndex = pageIndex;
    }

    internal void ActivateStoryInstance(Guid storyId, int pageIndex)
    {
        _surface?.Composition.Cancel();
        ActiveStoryPageIndex = pageIndex;
        Session.SwitchStory(storyId);
        _surface?.Refresh();
    }

    private bool ObserveActiveStory()
    {
        if (_observedStoryId == ActiveStoryId) return false;
        _observedStoryId = ActiveStoryId;
        if (ActiveStoryId == Guid.Empty || !Session.Document.Stories.TryGetValue(ActiveStoryId, out var active) ||
            active.Kind is not (DocumentStoryKind.Header or DocumentStoryKind.Footer)) _headerFooterSection = null;
        if (ActiveStoryId != Guid.Empty && Session.Document.Stories.TryGetValue(ActiveStoryId, out var story) &&
            story.Kind is DocumentStoryKind.Header or DocumentStoryKind.Footer &&
            !Session.Document.Sections.Any(s => s.Id == _headerFooterSection))
        {
            _editingFooter = story.Kind == DocumentStoryKind.Footer;
            for (var i = 0; i < Session.Document.Sections.Length; i++)
                foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
                    if (Session.Document.ResolveHeaderFooter(i, _editingFooter, variant)?.Id == ActiveStoryId)
                    { _headerFooterSection = Session.Document.Sections[i].Id; _headerFooterVariant = variant; break; }
        }
        CancelTableResize(); ClearTableCellSelection();
        _surface?.ResetStoryNavigation();
        ActiveStoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Edits first/even-page options and physical distances for the current section.</summary>
    public Task<bool> ShowHeaderFooterDialogAsync()
    {
        var section = Session.Document.Sections.FirstOrDefault(s => s.Id == _headerFooterSection) ?? Session.CurrentSection;
        var settings = section?.HeaderFooter ?? new HeaderFooterSettings();
        var dialog = new FormattingDialog("Headers and footers");
        dialog.Flag("Different first page", new(settings.DifferentFirstPage), value => settings = settings with { DifferentFirstPage = value });
        dialog.Flag("Different odd and even pages", new(settings.DifferentOddEvenPages), value => settings = settings with { DifferentOddEvenPages = value });
        dialog.NumberField("Header distance from top (DIP)", new(settings.HeaderDistance), 0, 10000, value => settings = settings with { HeaderDistance = value });
        dialog.NumberField("Footer distance from bottom (DIP)", new(settings.FooterDistance), 0, 10000, value => settings = settings with { FooterDistance = value });
        return ShowFormattingDialog(dialog, () => Session.SetHeaderFooterSettings(section?.Id ?? Guid.Empty, settings));
    }

    public Task<bool> ShowNoteSettingsDialogAsync(DocumentNoteKind kind)
    {
        var settings = kind == DocumentNoteKind.Footnote ? Session.Document.FootnoteSettings : Session.Document.EndnoteSettings;
        var dialog = new FormattingDialog(kind == DocumentNoteKind.Footnote ? "Footnotes" : "Endnotes");
        dialog.EnumField("Number format", new FormattingValue<PageNumberFormat>(settings.NumberFormat), value => settings = settings with { NumberFormat = value });
        dialog.NumberField("Start at", new(settings.Start), 1, 1000000, value => settings = settings with { Start = (int)value });
        var restart = dialog.EnumField("Restart numbering", new FormattingValue<NoteRestartPolicy>(settings.Restart), value => settings = settings with { Restart = value });
        var placement = dialog.EnumField("Placement", new FormattingValue<NotePlacement>(settings.Placement), value => settings = settings with { Placement = value });
        restart.ItemsSource = kind == DocumentNoteKind.Endnote ? new[] { NoteRestartPolicy.Continuous, NoteRestartPolicy.EachSection } : Enum.GetValues<NoteRestartPolicy>();
        placement.ItemsSource = kind == DocumentNoteKind.Endnote ? new[] { NotePlacement.DocumentEnd, NotePlacement.SectionEnd } : new[] { NotePlacement.PageBottom, NotePlacement.BelowText };
        restart.SelectedItem = settings.Restart; placement.SelectedItem = settings.Placement;
        dialog.TextField("Separator", new FormattingValue<string?>(settings.SeparatorText), value => settings = settings with { SeparatorText = value ?? "" });
        dialog.TextField("Continuation separator", new FormattingValue<string?>(settings.ContinuationSeparatorText), value => settings = settings with { ContinuationSeparatorText = value ?? "" });
        return ShowFormattingDialog(dialog, () => Session.SetNoteSettings(kind, settings));
    }
}
