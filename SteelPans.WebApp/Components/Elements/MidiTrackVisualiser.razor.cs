using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SteelPans.Shared.Music;
using SteelPans.WebApp.Services;

namespace SteelPans.WebApp.Components.Elements;

public enum MidiTrackVisualiserMode
{
    Display,
    Playback,
    Edit,
    Record
}
public enum NoteSnapDivision
{
    None,
    Bar,
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
    public EventCallback<RecordNoteMovePitchChange> MoveAndPitchNote { get; set; }

    [Parameter]
    public EventCallback<Guid> DeleteSelected { get; set; }

    private ElementReference root_;
    private DotNetObjectReference<MidiTrackVisualiser>? dotNetRef_;
    private bool rendered_;
    private int? lastDataHash_;
    private int? lastLayoutHash_;

    private bool IsEditMode => Mode == MidiTrackVisualiserMode.Edit;
    private bool IsRecordMode => Mode == MidiTrackVisualiserMode.Record;
    private bool IsRecordNoteMode => IsEditMode || IsRecordMode;

    private double VisualDurationSeconds => Math.Max(
        Duration.TotalSeconds > 0 ? Duration.TotalSeconds : DurationSeconds,
        0.01);

    private MidiPanEvent? SelectedRecordNote => Notes.FirstOrDefault(x => x.Id == SelectedNoteId);
    private bool ShouldPreventKeyDefault => IsEditMode && SelectedRecordNote is not null;
    private double StepSeconds => 60.0 / Math.Max(1, TempoBpm) / 4.0;

    private double NoteSnapValue
    {
        get
        {
            return NoteSnapDivision switch
            {
                NoteSnapDivision.None => 0,
                NoteSnapDivision.Bar => 1,
                NoteSnapDivision.Beat => BeatUnit,
                NoteSnapDivision.HalfBeat => 2 * BeatUnit,
                NoteSnapDivision.QuarterBeat => 4 * BeatUnit,
                NoteSnapDivision.EighthBeat => 8 * BeatUnit,
                NoteSnapDivision.SixteenthBeat => 16 * BeatUnit,
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

            return Notes.Select(x => x.Note.SemitoneNumber).DefaultIfEmpty(0).Min();
        }
    }

    private int VisualMaxSemitone
    {
        get
        {
            if (MaxSemitone is int max)
                return max;

            return Notes.Select(x => x.Note.SemitoneNumber).DefaultIfEmpty(VisualMinSemitone).Max();
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
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!rendered_)
            return;

        await RenderVisualiserIfChangedAsync();
    }


    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (!IsEditMode || !ShowEditTools || SelectedRecordNote is null)
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

            case "ArrowLeft":
                await MoveSelected.InvokeAsync(-StepSeconds);
                break;

            case "ArrowRight":
                await MoveSelected.InvokeAsync(StepSeconds);
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
        var layoutHash = GetLayoutHash();
        var dataHash = GetDataHash();

        if (!force && lastDataHash_ == dataHash)
            return;

        if (force || lastLayoutHash_ != layoutHash)
        {
            lastLayoutHash_ = layoutHash;
            lastDataHash_ = dataHash;
            await RenderVisualiserAsync();
            return;
        }

        lastDataHash_ = dataHash;
        await SyncVisualiserNotesAsync();
    }

    private async Task RenderVisualiserAsync()
    {
        var orderedNotes = BuildVisualiserNotes();

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

    private async Task SyncVisualiserNotesAsync()
    {
        await JS.InvokeVoidAsync(
            "visualiser.syncNotes",
            root_,
            BuildVisualiserNotes(),
            SelectedNoteId,
            PanLabel);
    }

    public async Task AddOrUpdateNoteAsync(MidiPanEvent note, bool select = false)
    {
        if (!rendered_)
            return;

        if (select)
            SelectedNoteId = note.Id;

        await JS.InvokeVoidAsync(
            "visualiser.addOrUpdateNote",
            root_,
            BuildVisualiserNote(note),
            select ? note.Id : SelectedNoteId,
            PanLabel);
    }

    public async Task StartPlayheadAsync(double positionSeconds, double? audioAnchorTimeSeconds = null)
    {
        if (!rendered_ || !ShouldShowPlayhead)
            return;

        await JS.InvokeVoidAsync(
            "visualiser.startPlayhead",
            root_,
            Math.Clamp(positionSeconds, 0.0, VisualDurationSeconds),
            audioAnchorTimeSeconds);
    }

    public async Task StopPlayheadAsync(double positionSeconds, bool resetViewport = false)
    {
        if (!rendered_ || !ShouldShowPlayhead)
            return;

        await JS.InvokeVoidAsync(
            "visualiser.stopPlayhead",
            root_,
            Math.Clamp(positionSeconds, 0.0, VisualDurationSeconds),
            resetViewport);
    }

    public async Task SetPlayheadPositionAsync(double positionSeconds, bool follow = true)
    {
        if (!rendered_ || !ShouldShowPlayhead)
            return;

        await JS.InvokeVoidAsync(
            "visualiser.setPosition",
            root_,
            Math.Clamp(positionSeconds, 0.0, VisualDurationSeconds),
            follow);
    }

    private bool ShouldShowPlayhead => Mode == MidiTrackVisualiserMode.Playback || ShowPlayhead;

    private IReadOnlyList<MidiTrackVisualiserNote> BuildVisualiserNotes()
    {
        return Notes
            .OrderBy(x => x.Start)
            .ThenBy(x => x.Note.SemitoneNumber)
            .Select(BuildVisualiserNote)
            .ToList();
    }

    private MidiTrackVisualiserNote BuildVisualiserNote(MidiPanEvent note)
    {
        return new MidiTrackVisualiserNote(
            note.Id,
            note.Note.ToString(),
            note.Note.SemitoneNumber,
            note.Start.TotalSeconds,
            note.Duration.TotalSeconds,
            note.Id == SelectedNoteId);
    }

    private int GetLayoutHash()
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
        hash.Add(VisualMinSemitone);
        hash.Add(VisualMaxSemitone);
        hash.Add(ShowEditTools);
        hash.Add(ShouldShowPlayhead);
        return hash.ToHashCode();
    }

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

        hash.Add(Notes.Count);
        foreach (var note in Notes)
        {
            hash.Add(note.Id);
            hash.Add(note.Note.SemitoneNumber);
            hash.Add(note.Start.Ticks);
            hash.Add(note.Duration.Ticks);
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
        if (!IsRecordNoteMode)
            return;

        await PositionChanged.InvokeAsync(Math.Clamp(seconds, 0.0, VisualDurationSeconds));
    }

    [JSInvokable]
    public async Task SelectRecordNote(string id)
    {
        if (!IsEditMode || !Guid.TryParse(id, out var noteId))
            return;

        await SelectNote.InvokeAsync(noteId);

        var note = Notes.FirstOrDefault(x => x.Id == noteId);
        if (note is not null)
            await PreviewNote.InvokeAsync(note.Note);
    }



    [JSInvokable]
    public async Task MoveRecordNote(string id, double deltaSeconds)
    {
        if (!IsEditMode || !Guid.TryParse(id, out var noteId))
            return;

        await SelectNote.InvokeAsync(noteId);
        await MoveSelected.InvokeAsync(deltaSeconds);
    }

    [JSInvokable]
    public async Task MoveRecordNoteAndPitch(string id, double deltaSeconds, int semitone)
    {
        if (!IsEditMode || !Guid.TryParse(id, out var noteId))
            return;

        var clampedSemitone = Math.Clamp(semitone, Math.Min(VisualMinSemitone, VisualMaxSemitone), Math.Max(VisualMinSemitone, VisualMaxSemitone));

        if (MoveAndPitchNote.HasDelegate)
        {
            await MoveAndPitchNote.InvokeAsync(new RecordNoteMovePitchChange(noteId, deltaSeconds, clampedSemitone));
        }
        else
        {
            await SelectNote.InvokeAsync(noteId);

            if (Math.Abs(deltaSeconds) > 0.0001)
                await MoveSelected.InvokeAsync(deltaSeconds);

            await SetSelectedPitch.InvokeAsync(clampedSemitone);
        }
    }

    [JSInvokable]
    public async Task MoveSelectedRecordNote(double deltaSeconds)
    {
        if (!IsEditMode || SelectedRecordNote is null)
            return;

        await MoveSelected.InvokeAsync(deltaSeconds);
    }

    [JSInvokable]
    public async Task ResizeSelectedRecordNote(double deltaSeconds)
    {
        if (!IsEditMode || SelectedRecordNote is null)
            return;

        await ResizeSelected.InvokeAsync(deltaSeconds);
    }

    [JSInvokable]
    public async Task ChangeSelectedRecordNotePitch(int semitoneDelta)
    {
        if (!IsEditMode || SelectedRecordNote is null)
            return;

        var semitone = Math.Clamp(
            SelectedRecordNote.Note.SemitoneNumber + semitoneDelta,
            Math.Min(VisualMinSemitone, VisualMaxSemitone),
            Math.Max(VisualMinSemitone, VisualMaxSemitone));

        await ChangeSelectedPitch.InvokeAsync(semitoneDelta);
        await PreviewNote.InvokeAsync(Note.FromSemitoneNumber(semitone));
    }

    [JSInvokable]
    public async Task PreviewRecordNoteSemitone(int semitone)
    {
        if (!IsEditMode)
            return;

        var clampedSemitone = Math.Clamp(
            semitone,
            Math.Min(VisualMinSemitone, VisualMaxSemitone),
            Math.Max(VisualMinSemitone, VisualMaxSemitone));

        await PreviewNote.InvokeAsync(Note.FromSemitoneNumber(clampedSemitone));
    }

    [JSInvokable]
    public async Task DeleteSelectedRecordNote()
    {
        if (!IsEditMode || SelectedRecordNote is null)
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

    public sealed record RecordNoteMovePitchChange(
        Guid Id,
        double DeltaSeconds,
        int Semitone);

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
