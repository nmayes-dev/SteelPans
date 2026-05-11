using Microsoft.JSInterop;
using SteelPans.WebApp.Components.Elements;
using SteelPans.WebApp.Model;

namespace SteelPans.WebApp.Services;

public sealed record MidiPlaybackStartedEventArgs(
    double StartAt,
    TimeSpan StartOffset,
    IReadOnlyList<MidiPanEvent> PlaybackEvents);

public sealed record MidiPlaybackPausedEventArgs(TimeSpan Position);

public sealed record MidiPlaybackStoppedEventArgs(bool ResetPosition, TimeSpan Position);

public sealed record PlaybackPositionChangedEventArgs(
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying);

public sealed record PlaybackTempoChangedEventArgs(int Bpm);

public sealed class MidiPlaybackService : IAsyncDisposable
{
    private readonly IJSRuntime js_;

    private readonly Dictionary<int, List<MidiPanEvent>> midiTrackEventsByIndex_ = [];
    private readonly Dictionary<Guid, SteelPanView> steelPanViews_ = [];

    private CancellationTokenSource? midiPlaybackCts_;
    private CancellationTokenSource? playbackProgressCts_;

    private MidiPlaybackInfo? midiPlaybackInfo_;
    private int? midiBpmOverride_;
    private double? midiStartAt_;

    private Guid? pendingRestartPanInstanceId_;
    private TimeSpan? pendingRestartOffset_;

    private TimeSpan playbackSessionStartOffset_ = TimeSpan.Zero;
    private double? playbackAudioAnchorTime_;
    private TimeSpan playbackScoreAnchorOffset_ = TimeSpan.Zero;
    private int playbackTempoAnchorBpm_ = 120;

    public MidiPlaybackService(IJSRuntime js)
    {
        js_ = js;
    }

    public event Func<Task>? StateChanged;
    public event Func<MidiPlaybackStartedEventArgs, Task>? PlaybackStarted;
    public event Func<MidiPlaybackPausedEventArgs, Task>? PlaybackPaused;
    public event Func<MidiPlaybackStoppedEventArgs, Task>? PlaybackStopped;
    public event Func<PlaybackPositionChangedEventArgs, Task>? PositionChanged;
    public event Func<PlaybackTempoChangedEventArgs, Task>? TempoChanged;

    public List<MidiTrackAssignment> Assignments { get; } = [];
    public List<MidiAssignedPan> ActivePans { get; } = [];

    public bool IsPlaying { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public double? MidiStartAt => midiStartAt_;

    public int TempoBpm { get; private set; } = 120;
    public int BeatsPerBar { get; private set; } = 4;
    public int BeatUnit { get; private set; } = 4;
    public bool ClickTrackEnabled { get; private set; }

    public int InitialMidiBpm => midiPlaybackInfo_?.InitialBpm ?? TempoBpm;
    public int EffectiveMidiBpm => midiBpmOverride_ ?? midiPlaybackInfo_?.InitialBpm ?? TempoBpm;

    public async Task LoadMidiAsync(MidiPlaybackInfo? playbackInfo, IReadOnlyDictionary<int, List<MidiPanEvent>> trackEventsByIndex)
    {
        await StopAsync(resetPosition: true);

        Assignments.Clear();
        ActivePans.Clear();
        steelPanViews_.Clear();
        midiTrackEventsByIndex_.Clear();

        foreach (var (index, events) in trackEventsByIndex)
            midiTrackEventsByIndex_[index] = events;

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

        await NotifyStateChangedAsync();
    }

    public async Task AddAssignmentAsync(MidiTrackAssignment assignment, IReadOnlyList<SteelPan> availablePans)
    {
        var restartOffset = IsPlaying
            ? await GetCurrentPositionAsync()
            : (TimeSpan?)null;

        Assignments.RemoveAll(x => x.Index == assignment.Index);
        ActivePans.RemoveAll(x => x.Index == assignment.Index);

        var assignedPan = BuildAssignedPan(assignment, availablePans);
        if (assignedPan is null)
            return;

        Assignments.Add(assignment);
        ActivePans.Add(assignedPan);
        RecalculateDuration();

        if (restartOffset is not null)
        {
            pendingRestartPanInstanceId_ = assignedPan.InstanceId;
            pendingRestartOffset_ = restartOffset.Value;
        }

        await NotifyStateChangedAsync();
    }

    public async Task RemoveAssignmentAsync(int index)
    {
        Assignments.RemoveAll(x => x.Index == index);

        if (!Assignments.Any())
            await StopAsync();

        foreach (var removedPan in ActivePans.Where(x => x.Index == index).ToList())
            steelPanViews_.Remove(removedPan.InstanceId);

        ActivePans.RemoveAll(x => x.Index == index);

        RecalculateDuration();
        Position = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        await NotifyStateChangedAsync();
    }

    public void UnregisterSteelPanView(Guid instanceId)
    {
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

        await js_.InvokeVoidAsync("steelPan.setComponentVolume", view.ComponentId, assignedPan.Volume);

        if (!IsPlaying)
            return;

        if (pendingRestartPanInstanceId_ == instanceId && pendingRestartOffset_ is not null)
        {
            var restartOffset = pendingRestartOffset_.Value;
            pendingRestartPanInstanceId_ = null;
            pendingRestartOffset_ = null;

            await RestartFromAsync(restartOffset);
            return;
        }

        var currentPosition = await GetCurrentPositionAsync();
        var playbackEvents = GetPlaybackEventsFromOffset(assignedPan.Events, currentPosition);

        if (playbackEvents.Count == 0)
            return;

        var currentAudioTime = await js_.InvokeAsync<double>("steelPan.getAudioTime");

        await view.StartMidiSequenceAtAsync(
            playbackEvents,
            currentAudioTime + 0.05,
            InitialMidiBpm,
            EffectiveMidiBpm,
            midiPlaybackCts_?.Token ?? CancellationToken.None);
    }

    public async Task SetPanVolumeAsync(MidiAssignedPan activePan, double volume)
    {
        activePan.Volume = Math.Clamp(volume, 0.0, 1.0);

        if (steelPanViews_.TryGetValue(activePan.InstanceId, out var view))
            await js_.InvokeVoidAsync("steelPan.setComponentVolume", view.ComponentId, activePan.Volume);

        await NotifyStateChangedAsync();
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
                Events = GetPlaybackEventsFromOffset(x.Events, startOffset)
            })
            .Where(x => x.Events.Count > 0)
            .ToList();

        if (playbackGroups.Count == 0)
        {
            IsPlaying = false;
            Position = Duration;
            playbackSessionStartOffset_ = Duration;
            await NotifyStateChangedAsync();
            return;
        }

        midiPlaybackCts_?.Cancel();
        midiPlaybackCts_?.Dispose();
        midiPlaybackCts_ = new CancellationTokenSource();

        StopPlaybackProgressLoop();
        playbackProgressCts_ = new CancellationTokenSource();

        IsPlaying = true;
        playbackSessionStartOffset_ = ClampPlaybackTime(startOffset);
        Position = playbackSessionStartOffset_;

        if (midiPlaybackInfo_ is not null)
        {
            TempoBpm = EffectiveMidiBpm;
            BeatsPerBar = midiPlaybackInfo_.InitialBeatsPerBar;
            BeatUnit = midiPlaybackInfo_.InitialBeatUnit;
        }

        try
        {
            var firstGroup = playbackGroups[0];
            if (!steelPanViews_.TryGetValue(firstGroup.Pan.InstanceId, out var firstView))
            {
                IsPlaying = false;
                await NotifyStateChangedAsync();
                return;
            }

            midiStartAt_ = await firstView.StartMidiSequenceAsync(firstGroup.Events, InitialMidiBpm, EffectiveMidiBpm, midiPlaybackCts_.Token);
            if (midiStartAt_ is null)
            {
                IsPlaying = false;
                await NotifyStateChangedAsync();
                return;
            }

            playbackAudioAnchorTime_ = midiStartAt_.Value;
            playbackScoreAnchorOffset_ = playbackSessionStartOffset_;
            playbackTempoAnchorBpm_ = EffectiveMidiBpm;

            foreach (var group in playbackGroups.Skip(1))
            {
                if (steelPanViews_.TryGetValue(group.Pan.InstanceId, out var view))
                    await view.StartMidiSequenceAtAsync(group.Events, midiStartAt_.Value, InitialMidiBpm, EffectiveMidiBpm, midiPlaybackCts_.Token);
            }

            await NotifyPlaybackStartedAsync(new MidiPlaybackStartedEventArgs(
                midiStartAt_.Value,
                playbackSessionStartOffset_,
                playbackGroups.SelectMany(x => x.Events).OrderBy(x => x.Start).ToList()));

            StartPlaybackProgressLoop(playbackProgressCts_.Token);
            await NotifyStateChangedAsync();
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
        await NotifyStateChangedAsync();
    }

    public async Task RestartFromAsync(TimeSpan startOffset)
    {
        await StopAsync(resetPosition: false);
        await PlayAsync(startOffset);
    }

    public async Task StopAsync(bool resetPosition = false)
    {
        midiPlaybackCts_?.Cancel();
        StopPlaybackProgressLoop();

        foreach (var steelPanView in steelPanViews_.Values)
            await steelPanView.StopMidiPlaybackAsync();

        midiStartAt_ = null;
        IsPlaying = false;
        playbackAudioAnchorTime_ = null;
        playbackTempoAnchorBpm_ = EffectiveMidiBpm;

        if (resetPosition)
        {
            Position = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;
        }

        await NotifyPlaybackStoppedAsync(new MidiPlaybackStoppedEventArgs(resetPosition, Position));
        await NotifyStateChangedAsync();
    }

    public async Task SetTempoBpmAsync(int bpm)
    {
        TempoBpm = bpm;

        if (midiPlaybackInfo_ is not null)
            midiBpmOverride_ = bpm;

        if (IsPlaying)
        {
            var currentPosition = await GetCurrentPositionAsync();
            var currentAudioTime = await js_.InvokeAsync<double>("steelPan.getAudioTime");

            Position = currentPosition;
            playbackScoreAnchorOffset_ = currentPosition;
            playbackAudioAnchorTime_ = currentAudioTime;
            playbackTempoAnchorBpm_ = bpm;

            foreach (var steelPanView in steelPanViews_.Values)
                await js_.InvokeVoidAsync("steelPan.updateMidiTempo", steelPanView.ComponentId, bpm);

            await NotifyTempoChangedAsync(new PlaybackTempoChangedEventArgs(bpm));
        }

        await NotifyStateChangedAsync();
    }

    public async Task SetBeatsPerBarAsync(int beatsPerBar)
    {
        BeatsPerBar = beatsPerBar;
        await NotifyStateChangedAsync();
    }

    public async Task SetBeatUnitAsync(int beatUnit)
    {
        BeatUnit = beatUnit;
        await NotifyStateChangedAsync();
    }

    public async Task SetClickTrackEnabledAsync(bool enabled)
    {
        if (ClickTrackEnabled == enabled)
            return;

        ClickTrackEnabled = enabled;

        if (ClickTrackEnabled && IsPlaying)
        {
            var currentPosition = await GetCurrentPositionAsync();
            var currentAudioTime = await js_.InvokeAsync<double>("steelPan.getAudioTime");

            var remainingEvents = ActivePans
                .SelectMany(x => GetPlaybackEventsFromOffset(x.Events, currentPosition))
                .OrderBy(x => x.Start)
                .ToList();

            await NotifyPlaybackStartedAsync(new MidiPlaybackStartedEventArgs(
                currentAudioTime + 0.05,
                currentPosition,
                remainingEvents));
        }

        await NotifyStateChangedAsync();
    }

    public async Task SeekToStartAsync()
    {
        Position = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        if (IsPlaying)
            await RestartFromAsync(TimeSpan.Zero);
        else
            await NotifyStateChangedAsync();

        await NotifyPositionChangedAsync();
    }

    public async Task GoToEndAsync()
    {
        if (IsPlaying)
            await StopAsync();

        Position = Duration;
        playbackSessionStartOffset_ = Duration;

        await NotifyStateChangedAsync();
        await NotifyPositionChangedAsync();
    }

    public async Task CommitSeekAsync(TimeSpan seekTime)
    {
        var clamped = ClampPlaybackTime(seekTime);

        Position = clamped;
        playbackSessionStartOffset_ = clamped;

        if (IsPlaying)
            await RestartFromAsync(clamped);
        else
            await NotifyStateChangedAsync();

        await NotifyPositionChangedAsync();
    }

    public async Task PreviewSeekAsync(TimeSpan previewTime)
    {
        Position = ClampPlaybackTime(previewTime);
        await NotifyPositionChangedAsync();
    }

    private MidiAssignedPan? BuildAssignedPan(MidiTrackAssignment assignment, IReadOnlyList<SteelPan> availablePans)
    {
        var sourcePan = availablePans.FirstOrDefault(x => x.PanType == assignment.AssignedPanType);
        if (sourcePan is null)
            return null;

        var rawEvents = midiTrackEventsByIndex_.GetValueOrDefault(assignment.Index) ?? [];
        var panInstance = ClonePan(sourcePan);
        var filteredEvents = PanMidiMapper.FilterToPan(panInstance, rawEvents);

        return new MidiAssignedPan
        {
            InstanceId = Guid.NewGuid(),
            Index = assignment.Index,
            Label = assignment.Label,
            PanType = assignment.AssignedPanType,
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

    private async Task<TimeSpan> GetCurrentPositionAsync(TimeSpan? baseOffset = null)
    {
        if (!IsPlaying || playbackAudioAnchorTime_ is null)
            return ClampPlaybackTime(baseOffset ?? playbackSessionStartOffset_);

        var currentAudioTime = await js_.InvokeAsync<double>("steelPan.getAudioTime");
        var elapsedAudioSeconds = Math.Max(0, currentAudioTime - playbackAudioAnchorTime_.Value);
        var elapsedScoreSeconds = elapsedAudioSeconds * GetPlaybackTempoRatio(playbackTempoAnchorBpm_);

        return ClampPlaybackTime(playbackScoreAnchorOffset_ + TimeSpan.FromSeconds(elapsedScoreSeconds));
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
                    await NotifyPositionChangedAsync();

                    if (Duration > TimeSpan.Zero && Position >= Duration)
                    {
                        IsPlaying = false;
                        playbackSessionStartOffset_ = Duration;
                        playbackAudioAnchorTime_ = null;
                        midiStartAt_ = null;

                        await NotifyPlaybackStoppedAsync(new MidiPlaybackStoppedEventArgs(false, Position));
                        await NotifyStateChangedAsync();
                        await NotifyPositionChangedAsync();

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

    private async Task NotifyPositionChangedAsync()
    {
        var handlers = PositionChanged;
        if (handlers is null)
            return;

        var args = new PlaybackPositionChangedEventArgs(Position, Duration, IsPlaying);

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

    private async Task NotifyStateChangedAsync()
    {
        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (Func<Task> handler in handlers.GetInvocationList())
            await handler();
    }

    private static SteelPan ClonePan(SteelPan source)
    {
        return new SteelPan
        {
            PanType = source.PanType,
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
    }
}