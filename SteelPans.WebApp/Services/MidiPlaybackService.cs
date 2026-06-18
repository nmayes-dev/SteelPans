using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.JSInterop;
using SteelPans.Shared.Ensembles;
using SteelPans.Shared.Music;
using SteelPans.Shared.Services;
using SteelPans.WebApp.Components.Elements;
using SteelPans.WebApp.Components.Pages;

namespace SteelPans.WebApp.Services;

public sealed record MidiPlaybackStartedEventArgs(
    double StartAt,
    TimeSpan StartOffset,
    IReadOnlyList<MidiPanEvent> PlaybackEvents);

public sealed record MidiPlaybackPausedEventArgs(TimeSpan Position);

public sealed record MidiPlaybackStoppedEventArgs(bool ResetPosition, TimeSpan Position);

public sealed record MidiFileLoadedEventArgs(
    string FileName,
    IReadOnlyList<MidiTrackInfo> Tracks,
    int InitialBpm,
    int InitialBeatsPerBar,
    int InitialBeatUnit);

public sealed record MidiFileUnloadedEventArgs();


public enum PlaybackAssignmentChangeOperation
{
    Add, Remove
}

public sealed record PlaybackAssignmentsChangedEventArgs(
    IReadOnlyList<MidiTrackAssignment> Assignments,
    IReadOnlyList<MidiAssignedPan> ActivePans,
    PlaybackAssignmentChangeOperation Operation);

public sealed record PanMixChangedEventArgs(IReadOnlyList<MidiAssignedPan> ActivePans);

public sealed record ClickTrackSettingsChangedEventArgs(
    int TempoBpm,
    int BeatsPerBar,
    int BeatUnit,
    bool Enabled);

public sealed record PlaybackPositionChangedEventArgs(
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    bool IsTick);

public sealed record PlaybackTempoChangedEventArgs(int Bpm);

public sealed record PlaybackCountInChangedEventArgs(int Count, int NoteDivision);

public sealed record MidiPlaybackJsState(
    bool IsPlaying,
    double PositionSeconds,
    double DurationSeconds,
    double? MidiStartAt,
    double? AudioAnchorTime,
    int InitialMidiBpm,
    int TempoBpm);

public sealed class MidiPlaybackService : IAsyncDisposable
{
    public sealed record MetronomeScheduleAction(
        double TimeSeconds,
        bool IsAccent,
        bool IsSubdivision = false);

    public sealed record MetronomeScheduleState(
        bool IsRunning,
        int Generation,
        double StartAt,
        double AudioTime,
        double ElapsedSeconds,
        double ScoreSeconds,
        double FirstActionTimeSeconds,
        int LastActionIndex,
        MetronomeScheduleAction? LastAction);

    private sealed record ServiceMetronomeAction(
        double TimeSeconds,
        bool IsAccent,
        bool IsSubdivision = false);

    private readonly MidiFileService midiFile_;
    private readonly DbService db_;
    private readonly TaskRunnerService tasks_;
    private readonly UserStateService state_;
    private readonly SafeJSInteropService js_;

    private IDisposable navCallback_;

        private readonly Dictionary<Guid, SteelPanView> steelPanViews_ = [];
    private readonly HashSet<string> playingComponentIds_ = [];

    private CancellationTokenSource? midiPlaybackCts_;
    private CancellationTokenSource? playbackProgressCts_;

    private MidiPlaybackInfo? midiPlaybackInfo_;
    private int? midiBpmOverride_;
    private double? midiStartAt_;

    private TimeSpan playbackSessionStartOffset_ = TimeSpan.Zero;
    private double? playbackAudioAnchorTime_;
    private TimeSpan playbackScoreAnchorOffset_ = TimeSpan.Zero;
    private int playbackTempoAnchorBpm_ = 120;

    public MidiPlaybackService(MidiFileService midiFile, DbService db, TaskRunnerService tasks, UserStateService state, SteelPanLoaderService panLoader, NavigationManager nav, SafeJSInteropService js)
    {
        midiFile_ = midiFile;
        db_ = db;
        tasks_ = tasks;
        state_ = state;
        js_ = js;

        AvailablePans = panLoader.Pans;

        navCallback_ = nav.RegisterLocationChangingHandler(OnNavigationAsync);
    }

    public event Func<MidiFileLoadedEventArgs, Task>? MidiFileLoaded;
    public event Func<MidiFileUnloadedEventArgs, Task>? MidiFileUnloaded;
    public event Func<PlaybackAssignmentsChangedEventArgs, Task>? AssignmentsChanged;
    public event Func<PanMixChangedEventArgs, Task>? PanMixChanged;
    public event Func<ClickTrackSettingsChangedEventArgs, Task>? ClickTrackSettingsChanged;
    public event Func<MidiPlaybackStartedEventArgs, Task>? PlaybackStarted;
    public event Func<MidiPlaybackPausedEventArgs, Task>? PlaybackPaused;
    public event Func<MidiPlaybackStoppedEventArgs, Task>? PlaybackStopped;
    public event Func<PlaybackPositionChangedEventArgs, Task>? PositionChanged;
    public event Func<PlaybackTempoChangedEventArgs, Task>? TempoChanged;
    public event Func<PlaybackCountInChangedEventArgs, Task>? CountInChanged;

    public IReadOnlyList<SteelPan> AvailablePans { get; } = [];
    public List<MidiTrackAssignment> Assignments { get; } = [];
    public List<MidiAssignedPan> ActivePans { get; private set; } = [];
    public List<MidiTrackInfo> Tracks { get; } = [];
    public string MidiFileName { get; private set; } = string.Empty;

    public bool IsMidiLoaded => midiPlaybackInfo_ is not null;

    public bool IsPlaying { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public double? MidiStartAt => midiStartAt_;

    public int TempoBpm { get; private set; } = 120;
    public int BeatsPerBar { get; private set; } = 4;
    public int BeatUnit { get; private set; } = 4;
    public bool ClickTrackEnabled { get; private set; }
    public int CountInBeats { get; private set; }
    public int CountInNoteDivision { get; private set; } = 4;

    public int InitialMidiBpm => midiPlaybackInfo_?.InitialBpm ?? TempoBpm;
    public int EffectiveMidiBpm => midiBpmOverride_ ?? midiPlaybackInfo_?.InitialBpm ?? TempoBpm;

    public IReadOnlyList<MidiPanEvent> GetMidiTrackEvents(Guid trackId)
    {
        return state_.GetActiveMidiTrackEvents(trackId);
    }

    public MidiAssignedPan? GetAssignedPanForTrack(Guid trackId)
    {
        return ActivePans.FirstOrDefault(x => x.Assignment.Track?.Id == trackId);
    }

    private static string GetHeadlessPlaybackComponentId(Guid instanceId)
    {
        return $"midi-playback-{instanceId:N}";
    }

    private string GetPlaybackComponentId(MidiAssignedPan assignedPan)
    {
        return steelPanViews_.TryGetValue(assignedPan.InstanceId, out var view)
            ? view.ComponentId
            : GetHeadlessPlaybackComponentId(assignedPan.InstanceId);
    }

    private async Task SetPlaybackComponentVolumeAsync(MidiAssignedPan assignedPan, double volume)
    {
        var clampedVolume = Math.Clamp(volume, 0.0, 1.0);
        var headlessComponentId = GetHeadlessPlaybackComponentId(assignedPan.InstanceId);

        await js_.InvokeVoidAsync(
            "panPlayback.setComponentVolume",
            headlessComponentId,
            clampedVolume);

        if (!steelPanViews_.TryGetValue(assignedPan.InstanceId, out var view) || view.ComponentId == headlessComponentId)
            return;

        await js_.InvokeVoidAsync(
            "panPlayback.setComponentVolume",
            view.ComponentId,
            clampedVolume);
    }

    private async Task ApplyPlaybackVolumesAsync()
    {
        var anySolo = ActivePans.Any(x => x.Soloing);

        foreach (var pan in ActivePans)
        {
            var volume = anySolo
                ? (pan.Soloing ? Math.Clamp(pan.Volume, 0.0, 1.0) : 0.0)
                : GetEffectivePanVolume(pan);

            await SetPlaybackComponentVolumeAsync(pan, volume);
        }
    }

    public async Task OnUnloadMidiAsync()
    {
        await StopAsync(resetPosition: true);

        Assignments.Clear();
        ActivePans.Clear();
        Tracks.Clear();

        steelPanViews_.Clear();
        playingComponentIds_.Clear();

                midiPlaybackInfo_ = null;
        midiBpmOverride_ = null;
        midiStartAt_ = null;

        playbackSessionStartOffset_ = TimeSpan.Zero;
        playbackAudioAnchorTime_ = null;
        playbackScoreAnchorOffset_ = TimeSpan.Zero;
        playbackTempoAnchorBpm_ = 120;

        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;

        state_.ClearActiveMidiFile();

        await NotifyMidiFileUnloadedAsync();
        await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Remove);
        await NotifyClickTrackSettingsChangedAsync();
        await NotifyPositionChangedAsync(jump: true);
        await PushPlaybackStateToJsAsync();
    }

    public async Task OnLoadMidiAsync(
        Func<Task<(string, MidiFile)>> getMidiFile,
        IReadOnlyList<MidiTrackDto>? persistedTracks = null)
    {
        await StopAsync(resetPosition: true);

        Assignments.Clear();
        ActivePans.Clear();
        Tracks.Clear();

        steelPanViews_.Clear();
        playingComponentIds_.Clear();
        
        var (fileName, midiFile) = await getMidiFile();

        MidiFileName = fileName;

        var playbackInfo = midiFile_.GetPlaybackInfo(midiFile);
        var playableTracks = midiFile_.LoadPlayableTracks(
            midiFile,
            persistedTracks?.OrderBy(x => x.TrackIndex)
                .Select(x => new MidiTrackSummary
                {
                    Id = x.Id,
                    Index = x.TrackIndex - 1,
                    Name = x.TrackName,
                    PanType = x.SuggestedPanType ?? PanType.None,
                    TempoBpm = playbackInfo.InitialBpm,
                    BeatsPerBar = playbackInfo.InitialBeatsPerBar,
                    BeatUnit = playbackInfo.InitialBeatUnit
                })
                .ToList());

        foreach (var track in playableTracks)
            Tracks.Add(track);

        state_.SetActiveMidiFile(fileName, Tracks, playbackInfo);

        midiPlaybackInfo_ = playbackInfo;
        midiBpmOverride_ = null;
        midiStartAt_ = null;
        playbackSessionStartOffset_ = TimeSpan.Zero;
        playbackAudioAnchorTime_ = null;
        playbackScoreAnchorOffset_ = TimeSpan.Zero;
        playbackTempoAnchorBpm_ = 120;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;

        if (midiPlaybackInfo_ is not null)
        {
            TempoBpm = midiPlaybackInfo_.InitialBpm;
            BeatsPerBar = midiPlaybackInfo_.InitialBeatsPerBar;
            BeatUnit = midiPlaybackInfo_.InitialBeatUnit;
        }

        await NotifyMidiFileLoadedAsync();
        await NotifyClickTrackSettingsChangedAsync();
        await NotifyPositionChangedAsync(jump: true);
        await PushPlaybackStateToJsAsync();
    }

    public async Task LoadGroupMidiFile(Guid fileId)
    {
        await tasks_.RunUnsafe(async () =>
        {
            var details = await db_.MidiFiles.GetMidiFileDetailsAsync(fileId);
            var download = await db_.MidiFiles.OpenMidiFileAsync(fileId);

            if (details is null || download is null)
                throw new FileNotFoundException("MIDI file was not found.");

            await OnLoadMidiAsync(
                async () =>
                {
                    await using var stream = download.Stream;
                    return (download.FileName, await midiFile_.OpenMidiFileAsync(stream));
                },
                details.Tracks);

            await LoadGroupMidiAssignments(details.Assignments);
        });
    }

    private async Task LoadGroupMidiAssignments(IReadOnlyList<MidiTrackAssignmentDto> assignments)
    {
        await OnClearAssignmentsAsync();

        foreach (var savedAssignment in assignments.Where(x => x.PanType != PanType.None))
        {
            var track = Tracks.FirstOrDefault(x => x.Id == savedAssignment.TrackId);

            if (track is null)
                continue;

            var assignment = new MidiTrackAssignment
            {
                AssignedPanType = savedAssignment.PanType,
                Track = track
            };

            await OnAddAssignmentAsync(assignment);
        }
    }

    public async Task OnClearAssignmentsAsync()
    {
        await StopAsync(resetPosition: true);

        Assignments.Clear();
        ActivePans.Clear();

        steelPanViews_.Clear();
        playingComponentIds_.Clear();

        playbackSessionStartOffset_ = TimeSpan.Zero;
        playbackAudioAnchorTime_ = null;
        playbackScoreAnchorOffset_ = TimeSpan.Zero;
        playbackTempoAnchorBpm_ = 120;

        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;

        await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Remove);
        await NotifyPositionChangedAsync(jump: true);
        await PushPlaybackStateToJsAsync();
    }

    public async Task OnAddAssignmentAsync(MidiTrackAssignment assignment)
    {
        Assignments.RemoveAll(x => x.Track?.Id == assignment.Track?.Id);
        ActivePans.RemoveAll(x => x.Assignment.Track?.Id == assignment.Track?.Id);

        var assignedPan = BuildAssignedPan(assignment, AvailablePans);
        if (assignedPan is null)
            return;

        Assignments.Add(assignment);
        ActivePans.Add(assignedPan);
        RecalculateDuration();

        await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Add);
        await PushPlaybackStateToJsAsync();
    }

    public async Task OnRemoveAssignmentAsync(Guid trackId)
    {
        Assignments.RemoveAll(x => x.Track?.Id == trackId);

        if (!Assignments.Any())
            await StopAsync();

        var toRemove = ActivePans.Where(x => x.Assignment.Track?.Id == trackId).ToList();
        foreach (var removedPan in toRemove)
            steelPanViews_.Remove(removedPan.InstanceId);

        ActivePans = ActivePans.Except(toRemove).ToList();

        RecalculateDuration();
        Position = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        await NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation.Remove);
        await NotifyPositionChangedAsync(jump: true);
        await PushPlaybackStateToJsAsync();
    }

    public void UnregisterSteelPanView(Guid instanceId)
    {
        if (steelPanViews_.TryGetValue(instanceId, out var view))
            playingComponentIds_.Remove(view.ComponentId);

        steelPanViews_.Remove(instanceId);
    }

    public async Task RegisterSteelPanViewAsync(Guid instanceId, SteelPanView? view)
    {
        if (view is null)
        {
            steelPanViews_.Remove(instanceId);
            return;
        }

        steelPanViews_[instanceId] = view;

        var assignedPan = ActivePans.FirstOrDefault(x => x.InstanceId == instanceId);
        if (assignedPan is null)
            return;

        if (!IsPlaying)
            return;

        await ApplyPlaybackVolumesAsync();

        var headlessComponentId = GetHeadlessPlaybackComponentId(instanceId);
        if (playingComponentIds_.Contains(headlessComponentId))
            await StopMidiSequenceAsync(headlessComponentId);

        await StartPanInCurrentPlaybackAsync(assignedPan, view);
    }

    private async Task StartPanInCurrentPlaybackAsync(MidiAssignedPan assignedPan, SteelPanView view)
    {
        if (!IsPlaying || midiPlaybackCts_ is null || playbackAudioAnchorTime_ is null)
            return;

        var currentAudioTime = await GetAudioTimeAsync();

        const double scheduleLeadSeconds = 0.8;

        var startAtAudioTime = currentAudioTime + scheduleLeadSeconds;
        var startAtPosition = GetCurrentPositionAtAudioTime(startAtAudioTime);

        var playbackEvents = GetPlaybackEventsFromOffset(assignedPan.Events, startAtPosition);

        if (playbackEvents.Count == 0)
            return;

        await view.ClearSelectionAndMidiVisualStateAsync();

        await StartMidiSequenceAsync(
            view,
            playbackEvents,
            midiPlaybackCts_.Token,
            startAtAudioTime);
    }

    public async Task SetPanVolumeAsync(MidiAssignedPan activePan, double volume)
    {
        activePan.Volume = Math.Clamp(volume, 0.0, 1.0);

        if (steelPanViews_.TryGetValue(activePan.InstanceId, out var view))
        {
            await js_.InvokeVoidAsync(
                "panPlayback.setComponentVolume",
                view.ComponentId,
                GetEffectivePanVolume(activePan));
        }

        await ApplyPlaybackVolumesAsync();
        await NotifyPanMixChangedAsync();
    }

    public async Task SetPanVolumesAsync(IEnumerable<MidiAssignedPan> pans, double volume)
    {
        foreach (var activePan in pans)
            activePan.Volume = Math.Clamp(volume, 0.0, 1.0);

        await ApplyPlaybackVolumesAsync();
        await NotifyPanMixChangedAsync();
    }

    public async Task SetPanMutedAsync(MidiAssignedPan activePan, bool muted)
    {
        if (activePan.Muted == muted)
            return;

        activePan.Muted = muted;

        await ApplyPlaybackVolumesAsync();
        await NotifyPanMixChangedAsync();
    }

    public async Task SetPanSoloingAsync(MidiAssignedPan activePan, bool solo)
    {
        if (activePan.Soloing == solo)
            return;

        activePan.Soloing = solo;

        if (solo)
        {
            activePan.Muted = false;

            foreach (var pan in ActivePans)
            {
                if (pan.InstanceId != activePan.InstanceId)
                    pan.Soloing = false;
            }
        }

        await ApplyPlaybackVolumesAsync();
        await NotifyPanMixChangedAsync();
    }

    public SteelPanView? GetInteractiveSteelPanView()
    {
        foreach (var assignedPan in ActivePans)
        {
            if (steelPanViews_.TryGetValue(assignedPan.InstanceId, out var assignedView))
                return assignedView;
        }

        return null;
    }

    public async Task SelectChordAsync(HashSet<int> pitchClasses)
    {
        var view = GetInteractiveSteelPanView();
        if (view is null)
            return;

        await view.SelectChordAsync(pitchClasses);
    }

    public async Task PlaySelectedNotesAsync()
    {
        var view = GetInteractiveSteelPanView();
        if (view is null)
            return;

        await view.PlaySelectedNotesAsync();
    }

    public async Task ToggleAsync()
    {
        if (IsPlaying)
            await PauseAsync();
        else
            await PlayAsync(playbackSessionStartOffset_);
    }

    public async Task PlayAsync(TimeSpan startOffset)
    {
        if (ActivePans.Count == 0)
            return;

        var playbackGroups = ActivePans
            .Select(x => new
            {
                Pan = x,
                ComponentId = GetPlaybackComponentId(x),
                Events = GetPlaybackEventsFromOffset(x.Events, startOffset)
            })
            .Where(x => x.Events.Count > 0)
            .ToList();

        if (playbackGroups.Count == 0)
        {
            IsPlaying = false;
            Position = Duration;
            playbackSessionStartOffset_ = Duration;
            await PushPlaybackStateToJsAsync();
            return;
        }

        midiPlaybackCts_?.Cancel();
        midiPlaybackCts_?.Dispose();
        midiPlaybackCts_ = new CancellationTokenSource();
        var playbackToken = midiPlaybackCts_.Token;

        StopPlaybackProgressLoop();
        playbackProgressCts_ = new CancellationTokenSource();

        IsPlaying = true;
        playbackSessionStartOffset_ = ClampPlaybackTime(startOffset);
        Position = playbackSessionStartOffset_;
        midiStartAt_ = null;

        await ApplyPlaybackVolumesAsync();

        if (midiPlaybackInfo_ is not null)
        {
            TempoBpm = EffectiveMidiBpm;
            BeatsPerBar = midiPlaybackInfo_.InitialBeatsPerBar;
            BeatUnit = midiPlaybackInfo_.InitialBeatUnit;
        }

        try
        {
            const double scheduleLeadSeconds = 0.8;

            var currentAudioTime = await GetAudioTimeAsync(playbackToken);
            playbackToken.ThrowIfCancellationRequested();

            var shouldCountIn = playbackSessionStartOffset_ <= TimeSpan.Zero;

            var countInDurationSeconds = shouldCountIn
                ? GetCountInDurationSeconds()
                : 0.0;

            var metronomeStartAt = currentAudioTime + scheduleLeadSeconds;
            var sharedStartAt = metronomeStartAt + countInDurationSeconds;

            await SchedulePlaybackMetronomeAsync(
                playbackGroups.SelectMany(x => x.Events).OrderBy(x => x.Start).ToList(),
                playbackSessionStartOffset_,
                metronomeStartAt,
                countInDurationSeconds,
                includeCountIn: shouldCountIn,
                includeClickTrack: ClickTrackEnabled,
                playbackToken);

            playbackToken.ThrowIfCancellationRequested();

            foreach (var group in playbackGroups)
            {
                if (steelPanViews_.TryGetValue(group.Pan.InstanceId, out var view))
                    await view.ClearSelectionAndMidiVisualStateAsync();
            }

            foreach (var group in playbackGroups)
            {
                playbackToken.ThrowIfCancellationRequested();

                var actualStartAt = await StartMidiSequenceAsync(
                    group.ComponentId,
                    group.Events,
                    playbackToken,
                    sharedStartAt);

                midiStartAt_ ??= actualStartAt;
            }

            if (midiStartAt_ is null)
            {
                await StopMetronomeAudioAsync();
                IsPlaying = false;
                await PushPlaybackStateToJsAsync();
                return;
            }

            playbackAudioAnchorTime_ = midiStartAt_.Value;
            playbackScoreAnchorOffset_ = playbackSessionStartOffset_;
            playbackTempoAnchorBpm_ = EffectiveMidiBpm;

            await NotifyPlaybackStartedAsync(new MidiPlaybackStartedEventArgs(
                midiStartAt_.Value,
                playbackSessionStartOffset_,
                playbackGroups.SelectMany(x => x.Events).OrderBy(x => x.Start).ToList()));

            StartPlaybackProgressLoop(playbackProgressCts_.Token);
            await PushPlaybackStateToJsAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task PauseAsync()
    {
        playbackSessionStartOffset_ = await GetCurrentPositionAsync();
        await StopAsync(resetPosition: false);
        Position = playbackSessionStartOffset_;
        await NotifyPlaybackPausedAsync(new MidiPlaybackPausedEventArgs(Position));
        await PushPlaybackStateToJsAsync();
    }

    public async Task RestartFromAsync(TimeSpan startOffset)
    {
        await StopAsync(resetPosition: false);
        await PlayAsync(startOffset);
    }

    public async Task StopAsync(bool resetPosition = false)
    {
        if (!IsPlaying)
            return;

        await StopMetronomeAudioAsync();

        midiPlaybackCts_?.Cancel();
        midiPlaybackCts_?.Dispose();
        midiPlaybackCts_ = null;

        StopPlaybackProgressLoop();

        var stopTasks = playingComponentIds_.Select(StopMidiSequenceAsync);
        var clearTasks = steelPanViews_.Values.Select(x => x.ClearMidiVisualStateAsync());

        await Task.WhenAll(Enumerable.Concat(stopTasks, clearTasks));

        playingComponentIds_.Clear();

        midiStartAt_ = null;
        IsPlaying = false;
        playbackAudioAnchorTime_ = null;
        playbackScoreAnchorOffset_ = Position;
        playbackTempoAnchorBpm_ = EffectiveMidiBpm;

        if (resetPosition)
        {
            Position = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;
            playbackScoreAnchorOffset_ = TimeSpan.Zero;
        }
        else
        {
            playbackSessionStartOffset_ = Position;
        }

        await NotifyPlaybackStoppedAsync(new MidiPlaybackStoppedEventArgs(resetPosition, Position));
        await PushPlaybackStateToJsAsync();
    }

    public async Task SetTempoBpmAsync(int bpm)
    {
        bpm = Math.Clamp(bpm, 20, 200);

        if (TempoBpm == bpm)
            return;

        TempoBpm = bpm;

        if (midiPlaybackInfo_ is not null)
            midiBpmOverride_ = bpm;

        if (IsPlaying)
        {
            var currentPosition = await GetCurrentPositionAsync();
            var currentAudioTime = await GetAudioTimeAsync();

            Position = currentPosition;
            playbackScoreAnchorOffset_ = currentPosition;
            playbackAudioAnchorTime_ = currentAudioTime;
            playbackTempoAnchorBpm_ = bpm;

            foreach (var componentId in playingComponentIds_)
                await js_.InvokeVoidAsync("panPlayback.updateMidiTempo", componentId, bpm);

            await RescheduleClickTrackAsync(currentPosition, currentAudioTime);
            await NotifyTempoChangedAsync(new PlaybackTempoChangedEventArgs(bpm));
        }
        else
        {
            await NotifyTempoChangedAsync(new PlaybackTempoChangedEventArgs(bpm));
        }

        await NotifyClickTrackSettingsChangedAsync();
        await PushPlaybackStateToJsAsync();
    }

    public async Task SetBeatsPerBarAsync(int beatsPerBar)
    {
        beatsPerBar = Math.Clamp(beatsPerBar, 1, 32);

        if (BeatsPerBar == beatsPerBar)
            return;

        BeatsPerBar = beatsPerBar;

        if (IsPlaying && ClickTrackEnabled)
            await RescheduleClickTrackAsync();

        await NotifyClickTrackSettingsChangedAsync();
    }

    public async Task SetBeatUnitAsync(int beatUnit)
    {
        beatUnit = beatUnit is 1 or 2 or 4 or 8 or 16 or 32
            ? beatUnit
            : 4;

        if (BeatUnit == beatUnit)
            return;

        BeatUnit = beatUnit;

        if (IsPlaying && ClickTrackEnabled)
            await RescheduleClickTrackAsync();

        await NotifyClickTrackSettingsChangedAsync();
    }

    public async Task SetClickTrackEnabledAsync(bool enabled)
    {
        if (ClickTrackEnabled == enabled)
            return;

        ClickTrackEnabled = enabled;

        if (IsPlaying)
        {
            await StopMetronomeAudioAsync();

            if (ClickTrackEnabled)
                await RescheduleClickTrackAsync();
        }

        await NotifyClickTrackSettingsChangedAsync();
        await PushPlaybackStateToJsAsync();
    }

    public async Task SetCountInBeatsAsync(int beats)
    {
        beats = Math.Clamp(beats, 0, 32);

        if (CountInBeats == beats)
            return;

        CountInBeats = beats;
        await NotifyCountInChangedAsync(new PlaybackCountInChangedEventArgs(CountInBeats, CountInNoteDivision));
        await PushPlaybackStateToJsAsync();
    }

    public async Task SetCountInNoteDivisionAsync(int noteDivision)
    {
        noteDivision = noteDivision switch
        {
            2 or 4 or 8 or 16 => noteDivision,
            _ => 4,
        };

        if (CountInNoteDivision == noteDivision)
            return;

        CountInNoteDivision = noteDivision;
        await NotifyCountInChangedAsync(new PlaybackCountInChangedEventArgs(CountInBeats, CountInNoteDivision));
        await PushPlaybackStateToJsAsync();
    }

    public async Task SeekToStartAsync()
    {
        Position = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        if (IsPlaying)
            await RestartFromAsync(TimeSpan.Zero);
        else
            await PushPlaybackStateToJsAsync();

        await NotifyPositionChangedAsync(jump: true);
    }

    public async Task GoToEndAsync()
    {
        if (IsPlaying)
            await StopAsync();

        Position = Duration;
        playbackSessionStartOffset_ = Duration;

        await NotifyPositionChangedAsync(jump: true);
        await PushPlaybackStateToJsAsync();
    }

    public async Task CommitSeekAsync(TimeSpan seekTime)
    {
        var clamped = ClampPlaybackTime(seekTime);

        Position = clamped;
        playbackSessionStartOffset_ = clamped;

        if (IsPlaying)
            await RestartFromAsync(clamped);
        else
            await PushPlaybackStateToJsAsync();

        await NotifyPositionChangedAsync(jump: true);
    }

    public async Task PreviewSeekAsync(TimeSpan previewTime)
    {
        Position = ClampPlaybackTime(previewTime);
        await NotifyPositionChangedAsync(jump: true);
    }


    public async Task<double> GetAudioTimeAsync(CancellationToken cancellationToken = default)
    {
        return await js_.InvokeAsync<double>("panPlayback.getAudioTime", cancellationToken);
    }

    public async Task BeginMetronomeWeightDragAsync(ElementReference trackElement, object dotNetRef, double initialClientY)
    {
        await js_.InvokeVoidAsync("panPlayback.beginMetronomeWeightDrag", trackElement, dotNetRef, initialClientY);
    }

    public async Task PlayMetronomeTickAsync(bool isAccent, CancellationToken cancellationToken = default)
    {
        await js_.InvokeVoidAsync("panPlayback.playMetronomeTick", cancellationToken, isAccent);
    }

    public async Task StopMetronomeAudioAsync()
    {
        await js_.InvokeVoidAsync("panPlayback.stopMetronome");
    }

    public async Task<MetronomeScheduleState?> GetMetronomeScheduleStateAsync(CancellationToken cancellationToken = default)
    {
        return await js_.InvokeAsync<MetronomeScheduleState?>(
            "panPlayback.getMetronomeScheduleState",
            cancellationToken);
    }

    private async ValueTask OnNavigationAsync(LocationChangingContext ctx)
    {
        await StopAsync(resetPosition: false);
    }

    private async Task<double?> StartMidiSequenceAsync(
        SteelPanView view,
        IReadOnlyList<MidiPanEvent> events,
        CancellationToken cancellationToken = default,
        double? startAt = null)
    {
        return await StartMidiSequenceAsync(
            view.ComponentId,
            events,
            cancellationToken,
            startAt);
    }

    private async Task<double?> StartMidiSequenceAsync(
        string componentId,
        IReadOnlyList<MidiPanEvent> events,
        CancellationToken cancellationToken = default,
        double? startAt = null)
    {
        if (events.Count == 0)
            return null;

        var playbackActions = BuildPlaybackActions(events);
        if (playbackActions.Count == 0)
            return null;

        var scheduledActions = BuildScheduledActions(playbackActions);

        cancellationToken.ThrowIfCancellationRequested();

        var actualStartAt = await js_.InvokeAsync<double?>(
            "panPlayback.playMidiSchedule",
            cancellationToken,
            componentId,
            scheduledActions,
            InitialMidiBpm,
            EffectiveMidiBpm,
            startAt);

        cancellationToken.ThrowIfCancellationRequested();

        if (actualStartAt is not null)
            playingComponentIds_.Add(componentId);

        return actualStartAt;
    }

    private async Task StopMidiSequenceAsync(string componentId)
    {
        if (!playingComponentIds_.Remove(componentId))
            return;

        await js_.InvokeVoidAsync("panPlayback.stopMidiSchedule", componentId);

        var view = steelPanViews_.Values.FirstOrDefault(x => x.ComponentId == componentId);
        if (view is not null)
            await view.ClearMidiVisualStateAsync();
    }


    private async Task RescheduleClickTrackAsync(TimeSpan? currentPosition = null, double? currentAudioTime = null)
    {
        await StopMetronomeAudioAsync();

        if (!IsPlaying || !ClickTrackEnabled)
            return;

        var audioTime = currentAudioTime ?? await GetAudioTimeAsync();
        var position = currentPosition ?? GetCurrentPositionAtAudioTime(audioTime);

        await SchedulePlaybackMetronomeAsync(
            GetRemainingPlaybackEvents(position),
            position,
            audioTime + 0.05,
            countInDurationSeconds: 0.0,
            includeCountIn: false,
            includeClickTrack: true);
    }

    private IReadOnlyList<MidiPanEvent> GetRemainingPlaybackEvents(TimeSpan playbackOffset)
    {
        return ActivePans
            .SelectMany(x => GetPlaybackEventsFromOffset(x.Events, playbackOffset))
            .OrderBy(x => x.Start)
            .ToList();
    }

    private async Task SchedulePlaybackMetronomeAsync(
        IReadOnlyList<MidiPanEvent> playbackEvents,
        TimeSpan playbackOffset,
        double startAt,
        double countInDurationSeconds,
        bool includeCountIn,
        bool includeClickTrack,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<ServiceMetronomeAction>();

        if (includeCountIn && CountInBeats > 0 && countInDurationSeconds > 0.0)
            actions.AddRange(BuildCountInActions(countInDurationSeconds));

        if (includeClickTrack)
        {
            actions.AddRange(BuildClickTrackActions(
                    playbackEvents,
                    TempoBpm,
                    BeatsPerBar,
                    BeatUnit,
                    playbackOffset)
                .Select(action => new ServiceMetronomeAction(
                    action.TimeSeconds + countInDurationSeconds,
                    action.IsAccent)));
        }

        if (actions.Count == 0)
            return;

        await js_.InvokeVoidAsync(
            "panPlayback.playMetronomeSchedule",
            cancellationToken,
            actions.OrderBy(x => x.TimeSeconds).ToList(),
            startAt,
            playbackOffset.TotalSeconds - countInDurationSeconds);
    }

    private List<ServiceMetronomeAction> BuildCountInActions(double countInDurationSeconds)
    {
        var actions = new List<ServiceMetronomeAction>();

        if (CountInBeats <= 0 || CountInNoteDivision <= 0 || TempoBpm <= 0 || BeatsPerBar <= 0 || BeatUnit <= 0)
            return actions;

        var secondsPerCountInNote = (60.0 / TempoBpm) * (4.0 / CountInNoteDivision);
        var secondsPerBeat = (60.0 / TempoBpm) * (4.0 / BeatUnit);
        var secondsPerBar = BeatsPerBar * secondsPerBeat;

        if (secondsPerCountInNote <= 0.0 || secondsPerBeat <= 0.0 || secondsPerBar <= 0.0)
            return actions;

        const double accentEpsilon = 0.000001;
        const double beatEpsilon = 0.000001;

        for (var i = 0; i < CountInBeats; i++)
        {
            var secondsFromPlaybackStart = (i - CountInBeats) * secondsPerCountInNote;
            var barPhase = PositiveModulo(secondsFromPlaybackStart, secondsPerBar);
            var beatPhase = PositiveModulo(secondsFromPlaybackStart, secondsPerBeat);

            var isAccent = barPhase <= accentEpsilon || Math.Abs(barPhase - secondsPerBar) <= accentEpsilon;
            var isBeat = beatPhase <= beatEpsilon || Math.Abs(beatPhase - secondsPerBeat) <= beatEpsilon;

            actions.Add(new ServiceMetronomeAction(
                i * secondsPerCountInNote,
                isAccent,
                IsSubdivision: !isBeat));
        }

        return actions;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        if (modulus <= 0.0)
            return 0.0;

        var result = value % modulus;
        return result < 0.0 ? result + modulus : result;
    }

    private static List<MidiPanPlaybackAction> BuildPlaybackActions(IEnumerable<MidiPanEvent> events)
    {
        return events
            .SelectMany(e => new[]
            {
                new MidiPanPlaybackAction
                {
                    Note = e.Note,
                    Time = e.Start,
                    IsNoteOn = true,
                },
                new MidiPanPlaybackAction
                {
                    Note = e.Note,
                    Time = e.End,
                    IsNoteOn = false,
                },
            })
            .OrderBy(a => a.Time)
            .ThenBy(a => a.IsNoteOn ? 1 : 0)
            .ToList();
    }

    private static List<MidiPanScheduledAction> BuildScheduledActions(IEnumerable<MidiPanPlaybackAction> actions)
    {
        return actions
            .Select(a => new MidiPanScheduledAction
            {
                NoteKey = a.Note.ToString(),
                TimeSeconds = a.Time.TotalSeconds,
                IsNoteOn = a.IsNoteOn,
            })
            .ToList();
    }

    private MidiAssignedPan? BuildAssignedPan(MidiTrackAssignment assignment, IReadOnlyList<SteelPan> availablePans)
    {
        var sourcePan = availablePans.FirstOrDefault(x => x.Type == assignment.AssignedPanType);
        if (sourcePan is null)
            return null;

        var rawEvents = assignment.Track?.Events ?? [];
        var panInstance = ClonePan(sourcePan);
        var filteredEvents = PanMidiMapper.FilterToPan(panInstance, rawEvents);

        return new MidiAssignedPan
        {
            InstanceId = Guid.NewGuid(),
            Assignment = assignment,
            Pan = panInstance,
            Events = filteredEvents,
        };
    }

    private double GetPlaybackTempoRatio(int bpm)
    {
        var sourceBpm = midiPlaybackInfo_?.InitialBpm ?? bpm;
        if (sourceBpm <= 0 || bpm <= 0)
            return 1.0;

        return (double)bpm / sourceBpm;
    }

    private IReadOnlyList<MidiPanEvent> GetPlaybackEventsFromOffset(IReadOnlyList<MidiPanEvent> sourceEvents, TimeSpan startOffset)
    {
        var playbackEvents = sourceEvents.OrderBy(x => x.Start).ToList();
        var clampedOffset = ClampPlaybackTime(startOffset);

        if (clampedOffset <= TimeSpan.Zero)
            return playbackEvents;

        return playbackEvents
            .Where(e => e.Start + e.Duration > clampedOffset)
            .Select(e =>
            {
                var effectiveStart = e.Start - clampedOffset;

                if (effectiveStart < TimeSpan.Zero)
                {
                    var trim = clampedOffset - e.Start;
                    var remainingDuration = e.Duration - trim;

                    if (remainingDuration <= TimeSpan.Zero)
                        remainingDuration = TimeSpan.FromMilliseconds(1);

                    return new MidiPanEvent
                    {
                        Note = e.Note,
                        Start = TimeSpan.Zero,
                        Duration = remainingDuration,
                    };
                }

                return new MidiPanEvent
                {
                    Note = e.Note,
                    Start = effectiveStart,
                    Duration = e.Duration,
                };
            })
            .ToList();
    }

    public static List<MetronomeAction> BuildClickTrackActions(
        IReadOnlyList<MidiPanEvent> playbackEvents,
        int bpm,
        int beatsPerBar,
        int beatUnit,
        TimeSpan playbackOffset)
    {
        var actions = new List<MetronomeAction>();

        if (playbackEvents.Count == 0 || bpm <= 0 || beatsPerBar <= 0 || beatUnit <= 0)
            return actions;

        var secondsPerBeat = (60.0 / bpm) * (4.0 / beatUnit);
        if (secondsPerBeat <= 0.0)
            return actions;

        var offsetSeconds = Math.Max(0.0, playbackOffset.TotalSeconds);
        var absoluteBeatPosition = offsetSeconds / secondsPerBeat;
        var completedBeats = (int)Math.Floor(absoluteBeatPosition);
        var beatPhase = absoluteBeatPosition - completedBeats;

        var firstActionDelay = beatPhase <= 0.000001
            ? 0.0
            : (1.0 - beatPhase) * secondsPerBeat;

        var firstAbsoluteBeatIndex = beatPhase <= 0.000001
            ? completedBeats
            : completedBeats + 1;

        var remainingDurationSeconds = playbackEvents.Max(e => e.Start + e.Duration).TotalSeconds;

        if (remainingDurationSeconds < firstActionDelay)
            return actions;

        var remainingAfterFirstBeat = remainingDurationSeconds - firstActionDelay;
        var beatCount = 1 + (int)Math.Floor(remainingAfterFirstBeat / secondsPerBeat);

        for (var i = 0; i < beatCount; i++)
        {
            var absoluteBeatIndex = firstAbsoluteBeatIndex + i;

            actions.Add(new MetronomeAction
            {
                TimeSeconds = firstActionDelay + (i * secondsPerBeat),
                IsAccent = absoluteBeatIndex % beatsPerBar == 0,
            });
        }

        return actions;
    }

    private double GetCountInDurationSeconds()
    {
        if (CountInBeats <= 0 || TempoBpm <= 0 || CountInNoteDivision <= 0)
            return 0.0;

        var secondsPerNote = (60.0 / TempoBpm) * (4.0 / CountInNoteDivision);
        return Math.Max(0.0, CountInBeats * secondsPerNote);
    }

    private TimeSpan GetCurrentPositionAtAudioTime(double audioTime, TimeSpan? baseOffset = null)
    {
        if (!IsPlaying || playbackAudioAnchorTime_ is null)
            return ClampPlaybackTime(baseOffset ?? playbackSessionStartOffset_);

        var elapsedAudioSeconds = Math.Max(0, audioTime - playbackAudioAnchorTime_.Value);
        var elapsedScoreSeconds = elapsedAudioSeconds * GetPlaybackTempoRatio(playbackTempoAnchorBpm_);

        return ClampPlaybackTime(playbackScoreAnchorOffset_ + TimeSpan.FromSeconds(elapsedScoreSeconds));
    }

    private async Task<TimeSpan> GetCurrentPositionAsync(TimeSpan? baseOffset = null)
    {
        var currentAudioTime = await GetAudioTimeAsync();
        return GetCurrentPositionAtAudioTime(currentAudioTime, baseOffset);
    }

    private TimeSpan ClampPlaybackTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            return TimeSpan.Zero;

        if (Duration > TimeSpan.Zero && time > Duration)
            return Duration;

        return time;
    }

    private void StartPlaybackProgressLoop(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));

                while (IsPlaying && await timer.WaitForNextTickAsync(cancellationToken))
                {
                    Position = await GetCurrentPositionAsync();
                    await NotifyPositionChangedAsync(jump: false);

                    if (Duration > TimeSpan.Zero && Position >= Duration)
                    {
                        IsPlaying = false;
                        playbackSessionStartOffset_ = Duration;
                        playbackAudioAnchorTime_ = null;
                        midiStartAt_ = null;

                        await NotifyPlaybackStoppedAsync(new MidiPlaybackStoppedEventArgs(false, Position));
                        await NotifyPositionChangedAsync(jump: false);
                        await PushPlaybackStateToJsAsync();

                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private void StopPlaybackProgressLoop()
    {
        playbackProgressCts_?.Cancel();
        playbackProgressCts_?.Dispose();
        playbackProgressCts_ = null;
    }

    private void RecalculateDuration()
    {
        var maxEnd = ActivePans
            .SelectMany(x => x.Events)
            .DefaultIfEmpty()
            .Max(x => x is null ? TimeSpan.Zero : x.Start + x.Duration);

        Duration = maxEnd;
        Position = ClampPlaybackTime(Position);
        playbackSessionStartOffset_ = ClampPlaybackTime(playbackSessionStartOffset_);
        playbackScoreAnchorOffset_ = ClampPlaybackTime(playbackScoreAnchorOffset_);
    }

    private async Task NotifyCountInChangedAsync(PlaybackCountInChangedEventArgs args)
    {
        var handlers = CountInChanged;
        if (handlers is null)
            return;

        foreach (Func<PlaybackCountInChangedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyPlaybackStartedAsync(MidiPlaybackStartedEventArgs args)
    {
        var handlers = PlaybackStarted;
        if (handlers is null)
            return;

        foreach (Func<MidiPlaybackStartedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyPlaybackPausedAsync(MidiPlaybackPausedEventArgs args)
    {
        var handlers = PlaybackPaused;
        if (handlers is null)
            return;

        foreach (Func<MidiPlaybackPausedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyPlaybackStoppedAsync(MidiPlaybackStoppedEventArgs args)
    {
        var handlers = PlaybackStopped;
        if (handlers is null)
            return;

        foreach (Func<MidiPlaybackStoppedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyPositionChangedAsync(bool jump)
    {
        var handlers = PositionChanged;
        if (handlers is null)
            return;

        var args = new PlaybackPositionChangedEventArgs(Position, Duration, IsPlaying, !jump);

        foreach (Func<PlaybackPositionChangedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyTempoChangedAsync(PlaybackTempoChangedEventArgs args)
    {
        var handlers = TempoChanged;
        if (handlers is null)
            return;

        foreach (Func<PlaybackTempoChangedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyMidiFileLoadedAsync()
    {
        var handlers = MidiFileLoaded;
        if (handlers is null)
            return;

        var args = new MidiFileLoadedEventArgs(
            MidiFileName,
            Tracks.ToList(),
            InitialMidiBpm,
            BeatsPerBar,
            BeatUnit);

        foreach (Func<MidiFileLoadedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyMidiFileUnloadedAsync()
    {
        var handlers = MidiFileUnloaded;
        if (handlers is null)
            return;

        var args = new MidiFileUnloadedEventArgs();

        foreach (Func<MidiFileUnloadedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyAssignmentsChangedAsync(PlaybackAssignmentChangeOperation operation)
    {
        var handlers = AssignmentsChanged;
        if (handlers is null)
            return;

        var args = new PlaybackAssignmentsChangedEventArgs(
            Assignments.ToList(),
            ActivePans.ToList(),
            operation);

        foreach (Func<PlaybackAssignmentsChangedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyPanMixChangedAsync()
    {
        var handlers = PanMixChanged;
        if (handlers is null)
            return;

        var args = new PanMixChangedEventArgs(ActivePans.ToList());

        foreach (Func<PanMixChangedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }

    private async Task NotifyClickTrackSettingsChangedAsync()
    {
        var handlers = ClickTrackSettingsChanged;
        if (handlers is null)
            return;

        var args = new ClickTrackSettingsChangedEventArgs(
            TempoBpm,
            BeatsPerBar,
            BeatUnit,
            ClickTrackEnabled);

        foreach (Func<ClickTrackSettingsChangedEventArgs, Task> handler in handlers.GetInvocationList())
            await handler(args);
    }


    private async Task PushPlaybackStateToJsAsync()
    {
        try
        {
            await js_.InvokeVoidAsync(
                "panPlayback.setMidiPlaybackState",
                new MidiPlaybackJsState(
                    IsPlaying,
                    Position.TotalSeconds,
                    Math.Max(Duration.TotalSeconds, 0.01),
                    MidiStartAt,
                    playbackAudioAnchorTime_,
                    Math.Max(InitialMidiBpm, 1),
                    Math.Max(TempoBpm, 1)));
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private double GetEffectivePanVolume(MidiAssignedPan activePan)
    {
        var anyOtherSolo = !activePan.Soloing && ActivePans.Any(p => p.InstanceId != activePan.InstanceId && p.Soloing);
        return (activePan.Muted || anyOtherSolo) ? 0.0 : activePan.Volume;
    }

    private static SteelPan ClonePan(SteelPan source)
    {
        return new SteelPan
        {
            Type = source.Type,
            Notes = source.Notes
                .Select(n => new PanNote
                {
                    Note = n.Note,
                })
                .ToList(),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(resetPosition: true);
        midiPlaybackCts_?.Dispose();
        playbackProgressCts_?.Dispose();

        navCallback_?.Dispose();
    }
}