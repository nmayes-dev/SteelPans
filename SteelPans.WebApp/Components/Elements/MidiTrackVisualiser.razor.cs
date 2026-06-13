using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Services;

namespace SteelPans.WebApp.Components.Elements;

public enum MidiTrackVisualiserMode
{
    Playback,
    RecordEdit
}
public enum NoteSnapDivision
{
    None,
    Barline,
    Beat,
    HalfBeat,
    QuarterBeat,
    EighthBeat,
    SixteenthBeat
}

public partial class MidiTrackVisualiser
{
    [Parameter]
    public MidiTrackVisualiserMode Mode { get; set; } = MidiTrackVisualiserMode.Playback;

    [Parameter]
    public int TrackIndex { get; set; }

    [Parameter]
    public string PanLabel { get; set; } = "Unassigned";

    [Parameter]
    public string? TrackLabel { get; set; }

    [Parameter]
    public IReadOnlyList<MidiPanEvent> Notes { get; set; } = [];

    [Parameter]
    public IReadOnlyList<RecordNoteDraft> RecordNotes { get; set; } = [];

    [Parameter]
    public Guid? SelectedNoteId { get; set; }

    [Parameter]
    public TimeSpan Duration { get; set; }

    [Parameter]
    public double DurationSeconds { get; set; }

    [Parameter]
    public int TempoBpm { get; set; } = 120;

    [Parameter]
    public int InitialMidiBpm { get; set; } = 120;

    [Parameter]
    public int BeatsPerBar { get; set; } = 4;

    [Parameter]
    public int BeatUnit { get; set; } = 4;

    [Parameter]
    public bool ShowEditTools { get; set; } = true;

    [Parameter]
    public bool ShowPlayhead { get; set; }

    [Parameter]
    public double PositionSeconds { get; set; }

    [Parameter]
    public NoteSnapDivision NoteSnapDivision { get; set; }

    [Parameter]
    public int? MinSemitone { get; set; }

    [Parameter]
    public int? MaxSemitone { get; set; }

    [Parameter]
    public EventCallback<Guid> SelectNote { get; set; }

    [Parameter]
    public EventCallback<Note> PreviewNote { get; set; }

    [Parameter]
    public EventCallback<double> PositionChanged { get; set; }

    [Parameter]
    public EventCallback<double> MoveSelected { get; set; }

    [Parameter]
    public EventCallback<double> ResizeSelected { get; set; }

    [Parameter]
    public EventCallback<int> ChangeSelectedPitch { get; set; }

    [Parameter]
    public EventCallback<int> SetSelectedPitch { get; set; }

    [Parameter]
    public EventCallback<Guid> DeleteSelected { get; set; }

    private ElementReference root_;
    private DotNetObjectReference<MidiTrackVisualiser>? dotNetRef_;
    private bool rendered_;
    private int? lastDataHash_;

    private int VisualNoteCount => Mode == MidiTrackVisualiserMode.RecordEdit
        ? RecordNotes.Count
        : Notes.Count;

    private double VisualDurationSeconds => Math.Max(
        Duration.TotalSeconds > 0 ? Duration.TotalSeconds : DurationSeconds,
        0.01);

    private RecordNoteDraft? SelectedRecordNote => RecordNotes.FirstOrDefault(x => x.Id == SelectedNoteId);
    private bool ShouldPreventKeyDefault => Mode == MidiTrackVisualiserMode.RecordEdit && SelectedRecordNote is not null;
    private double StepSeconds => 60.0 / Math.Max(1, TempoBpm) / 4.0;

    private double NoteSnapValue
    {
        get
        {
            double beat = 1.0 / BeatUnit;
            return NoteSnapDivision switch
            {
                NoteSnapDivision.None => 0,
                NoteSnapDivision.Barline => BeatsPerBar,
                NoteSnapDivision.Beat => beat,
                NoteSnapDivision.HalfBeat => 0.5 * beat,
                NoteSnapDivision.QuarterBeat => 0.25 * beat,
                NoteSnapDivision.EighthBeat => 0.125 * beat,
                NoteSnapDivision.SixteenthBeat => 0.0625 * beat,
                _ => 0.0
            };
        }
    }

    private int VisualMinSemitone
    {
        get
        {
            if (MinSemitone is int min)
                return min;

            var source = Mode == MidiTrackVisualiserMode.RecordEdit
                ? RecordNotes.Select(x => x.Note.SemitoneNumber)
                : Notes.Select(x => x.Note.SemitoneNumber);

            return source.DefaultIfEmpty(0).Min();
        }
    }

    private int VisualMaxSemitone
    {
        get
        {
            if (MaxSemitone is int max)
                return max;

            var source = Mode == MidiTrackVisualiserMode.RecordEdit
                ? RecordNotes.Select(x => x.Note.SemitoneNumber)
                : Notes.Select(x => x.Note.SemitoneNumber);

            return source.DefaultIfEmpty(VisualMinSemitone).Max();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        dotNetRef_ = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("visualiser.initialize", root_, dotNetRef_);
        rendered_ = true;
        await RenderVisualiserIfChangedAsync(force: true);
        await UpdatePlayheadPositionAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!rendered_)
            return;

        await RenderVisualiserIfChangedAsync();
        await UpdatePlayheadPositionAsync();
    }


    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || !ShowEditTools || SelectedRecordNote is null)
            return;

        switch (args.Key)
        {
            case "+":
            case "=":
                await ResizeSelected.InvokeAsync(StepSeconds);
                break;

            case "-":
            case "_":
                await ResizeSelected.InvokeAsync(-StepSeconds);
                break;

            case "ArrowUp":
                await ChangeSelectedPitch.InvokeAsync(1);
                break;

            case "ArrowDown":
                await ChangeSelectedPitch.InvokeAsync(-1);
                break;

            case "Delete":
            case "Backspace":
                await DeleteSelected.InvokeAsync(SelectedRecordNote.Id);
                break;
        }
    }

    private async Task RenderVisualiserIfChangedAsync(bool force = false)
    {
        var dataHash = GetDataHash();
        if (!force && lastDataHash_ == dataHash)
            return;

        lastDataHash_ = dataHash;
        await RenderVisualiserAsync();
    }

    private async Task RenderVisualiserAsync()
    {
        var orderedNotes = Mode == MidiTrackVisualiserMode.RecordEdit
            ? RecordNotes
                .OrderBy(x => x.StartSeconds)
                .ThenBy(x => x.Note.SemitoneNumber)
                .Select(x => new MidiTrackVisualiserNote(
                    x.Id,
                    x.Note.ToString(),
                    x.Note.SemitoneNumber,
                    x.StartSeconds,
                    x.DurationSeconds,
                    x.Id == SelectedNoteId))
                .ToList()
            : Notes
                .OrderBy(x => x.Start)
                .ThenBy(x => x.Note.SemitoneNumber)
                .Select(x => new MidiTrackVisualiserNote(
                    null,
                    x.Note.ToString(),
                    x.Note.SemitoneNumber,
                    x.Start.TotalSeconds,
                    x.Duration.TotalSeconds,
                    false))
                .ToList();

        await JS.InvokeVoidAsync(
            "visualiser.setData",
            root_,
            new MidiTrackVisualiserData(
                Mode.ToString(),
                TrackIndex,
                PanLabel,
                TrackLabel,
                orderedNotes,
                VisualDurationSeconds,
                Math.Max(TempoBpm, 1),
                Math.Max(InitialMidiBpm, 1),
                Math.Max(BeatsPerBar, 1),
                Math.Max(BeatUnit, 1),
                Math.Max(NoteSnapValue, 0),
                SelectedNoteId,
                Math.Min(VisualMinSemitone, VisualMaxSemitone),
                Math.Max(VisualMinSemitone, VisualMaxSemitone),
                ShouldShowPlayhead));
    }

    private async Task UpdatePlayheadPositionAsync()
    {
        if (!ShouldUseExternalPosition)
            return;

        await JS.InvokeVoidAsync(
            "visualiser.setPosition",
            root_,
            Math.Clamp(PositionSeconds, 0.0, VisualDurationSeconds));
    }

    private bool ShouldShowPlayhead => Mode == MidiTrackVisualiserMode.Playback || ShowPlayhead;
    private bool ShouldUseExternalPosition => Mode == MidiTrackVisualiserMode.RecordEdit && ShowPlayhead;

    private int GetDataHash()
    {
        var hash = new HashCode();
        hash.Add(Mode);
        hash.Add(TrackIndex);
        hash.Add(PanLabel);
        hash.Add(TrackLabel);
        hash.Add(VisualDurationSeconds);
        hash.Add(TempoBpm);
        hash.Add(InitialMidiBpm);
        hash.Add(BeatsPerBar);
        hash.Add(BeatUnit);
        hash.Add(NoteSnapDivision);
        hash.Add(SelectedNoteId);
        hash.Add(VisualMinSemitone);
        hash.Add(VisualMaxSemitone);
        hash.Add(ShowEditTools);
        hash.Add(ShouldShowPlayhead);

        if (Mode == MidiTrackVisualiserMode.RecordEdit)
        {
            hash.Add(RecordNotes.Count);
            foreach (var note in RecordNotes)
            {
                hash.Add(note.Id);
                hash.Add(note.Note.SemitoneNumber);
                hash.Add(note.StartSeconds);
                hash.Add(note.DurationSeconds);
            }
        }
        else
        {
            hash.Add(Notes.Count);
            foreach (var note in Notes)
            {
                hash.Add(note.Note.SemitoneNumber);
                hash.Add(note.Start.Ticks);
                hash.Add(note.Duration.Ticks);
            }
        }

        return hash.ToHashCode();
    }

    [JSInvokable]
    public async Task PreviewSeekSeconds(double seconds)
    {
        if (Mode != MidiTrackVisualiserMode.Playback)
            return;

        await Playback.PreviewSeekAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)));
    }

    [JSInvokable]
    public async Task CommitSeekSeconds(double seconds)
    {
        if (Mode != MidiTrackVisualiserMode.Playback)
            return;

        await Playback.CommitSeekAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)));
    }


    [JSInvokable]
    public async Task SetRecordPositionSeconds(double seconds)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit)
            return;

        await PositionChanged.InvokeAsync(Math.Clamp(seconds, 0.0, VisualDurationSeconds));
    }

    [JSInvokable]
    public async Task SelectRecordNote(string id)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || !Guid.TryParse(id, out var noteId))
            return;

        await SelectNote.InvokeAsync(noteId);

        var note = RecordNotes.FirstOrDefault(x => x.Id == noteId);
        if (note is not null)
            await PreviewNote.InvokeAsync(note.Note);
    }



    [JSInvokable]
    public async Task MoveRecordNote(string id, double deltaSeconds)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || !Guid.TryParse(id, out var noteId))
            return;

        await SelectNote.InvokeAsync(noteId);
        await MoveSelected.InvokeAsync(deltaSeconds);
    }

    [JSInvokable]
    public async Task MoveRecordNoteAndPitch(string id, double deltaSeconds, int semitone)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || !Guid.TryParse(id, out var noteId))
            return;

        await SelectNote.InvokeAsync(noteId);

        if (Math.Abs(deltaSeconds) > 0.0001)
            await MoveSelected.InvokeAsync(deltaSeconds);

        var clampedSemitone = Math.Clamp(semitone, Math.Min(VisualMinSemitone, VisualMaxSemitone), Math.Max(VisualMinSemitone, VisualMaxSemitone));
        await SetSelectedPitch.InvokeAsync(clampedSemitone);
        await PreviewNote.InvokeAsync(Note.FromSemitoneNumber(clampedSemitone));
    }

    [JSInvokable]
    public async Task MoveSelectedRecordNote(double deltaSeconds)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || SelectedRecordNote is null)
            return;

        await MoveSelected.InvokeAsync(deltaSeconds);
    }

    [JSInvokable]
    public async Task ResizeSelectedRecordNote(double deltaSeconds)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || SelectedRecordNote is null)
            return;

        await ResizeSelected.InvokeAsync(deltaSeconds);
    }

    [JSInvokable]
    public async Task ChangeSelectedRecordNotePitch(int semitoneDelta)
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || SelectedRecordNote is null)
            return;

        var semitone = Math.Clamp(
            SelectedRecordNote.Note.SemitoneNumber + semitoneDelta,
            Math.Min(VisualMinSemitone, VisualMaxSemitone),
            Math.Max(VisualMinSemitone, VisualMaxSemitone));

        await ChangeSelectedPitch.InvokeAsync(semitoneDelta);
        await PreviewNote.InvokeAsync(Note.FromSemitoneNumber(semitone));
    }

    [JSInvokable]
    public async Task DeleteSelectedRecordNote()
    {
        if (Mode != MidiTrackVisualiserMode.RecordEdit || SelectedRecordNote is null)
            return;

        await DeleteSelected.InvokeAsync(SelectedRecordNote.Id);
    }

    public async ValueTask DisposeAsync()
    {
        if (dotNetRef_ is null)
            return;

        try
        {
            await JS.InvokeVoidAsync("visualiser.dispose", root_);
        }
        catch (JSDisconnectedException)
        {
        }

        dotNetRef_?.Dispose();
        dotNetRef_ = null;
    }

    private sealed record MidiTrackVisualiserNote(
        Guid? Id,
        string Note,
        int Semitone,
        double StartSeconds,
        double DurationSeconds,
        bool IsSelected);

    private sealed record MidiTrackVisualiserData(
        string Mode,
        int TrackIndex,
        string PanLabel,
        string? TrackLabel,
        IReadOnlyList<MidiTrackVisualiserNote> Notes,
        double DurationSeconds,
        int TempoBpm,
        int InitialMidiBpm,
        int BeatsPerBar,
        int BeatUnit,
        double SnapNoteDivision,
        Guid? SelectedNoteId,
        int MinSemitone,
        int MaxSemitone,
        bool ShowPlayhead);
}
