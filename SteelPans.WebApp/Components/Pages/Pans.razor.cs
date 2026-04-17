using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using SteelPans.WebApp.Components.Elements;
using SteelPans.WebApp.Model;
using SteelPans.WebApp.Services;

namespace SteelPans.WebApp.Components.Pages;

public partial class Pans
{
    private readonly List<SteelPan> pans_ = [];
    private readonly List<PanType> availablePanTypes_ = [];
    private readonly List<MidiTrackInfo> midiTracks_ = [];
    private readonly Dictionary<int, List<MidiPanEvent>> midiTrackEventsByIndex_ = [];
    private readonly Dictionary<Guid, SteelPanView> steelPanViews_ = [];

    private int selectedPanIndex_;
    private string? loadError_;

    private IBrowserFile? midiFile_;
    private MidiPlaybackInfo? midiPlaybackInfo_;
    private List<MidiTrackAssignment> midiTrackAssignments_ = [];
    private List<MidiAssignedPan> activeMidiPans_ = [];

    private CancellationTokenSource? midiPlaybackCts_;
    private CancellationTokenSource? playbackProgressCts_;

    private bool isMidiPlaying_;
    private double? midiStartAt_;

    private int metronomeBpm_ = 120;
    private int metronomeBeatsPerBar_ = 4;
    private int metronomeBeatUnit_ = 4;
    private bool metronomeEnabled_;
    private int? midiBpmOverride_;

    private TimeSpan playbackPosition_;
    private TimeSpan playbackDuration_;
    private TimeSpan playbackSessionStartOffset_ = TimeSpan.Zero;

    private bool showToolsMenu_;
    private bool showChordBuilderPanel_;
    private bool showMetronomePanel_;

    private SteelPanView? previewSteelPanView_;
    private Metronome? metronome_;

    private SteelPan? SelectedPan =>
        selectedPanIndex_ >= 0 && selectedPanIndex_ < pans_.Count
            ? pans_[selectedPanIndex_]
            : null;

    private int EffectiveMidiBpm =>
        midiBpmOverride_
        ?? midiPlaybackInfo_?.InitialBpm
        ?? metronomeBpm_;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            pans_.Clear();
            pans_.AddRange(await PanLoader.LoadAsync());

            availablePanTypes_.Clear();
            availablePanTypes_.AddRange(pans_
                .Select(x => x.PanType)
                .Where(x => x != PanType.None)
                .Distinct()
                .OrderBy(x => x.ToString()));

            selectedPanIndex_ = 0;
        }
        catch (Exception ex)
        {
            loadError_ = ex.Message;
        }
    }

    private async Task OnMidiSelectedAsync(IBrowserFile file)
    {
        await StopMidiAsync(resetPosition: true);

        midiFile_ = file;
        midiTracks_.Clear();
        midiTrackAssignments_.Clear();
        midiTrackEventsByIndex_.Clear();
        activeMidiPans_.Clear();
        steelPanViews_.Clear();
        midiPlaybackInfo_ = null;
        midiBpmOverride_ = null;
        midiStartAt_ = null;
        playbackPosition_ = TimeSpan.Zero;
        playbackDuration_ = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        await using (var playbackInfoStream = midiFile_.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024))
        {
            midiPlaybackInfo_ = await MidiLoader.GetPlaybackInfoAsync(playbackInfoStream);
        }

        if (midiPlaybackInfo_ is not null)
        {
            metronomeBpm_ = midiPlaybackInfo_.InitialBpm;
            metronomeBeatsPerBar_ = midiPlaybackInfo_.InitialBeatsPerBar;
            metronomeBeatUnit_ = midiPlaybackInfo_.InitialBeatUnit;
        }

        await using (var trackStream = midiFile_.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024))
        {
            var playableTracks = await MidiLoader.LoadPlayableTracksAsync(trackStream);

            foreach (var (track, events) in playableTracks)
            {
                midiTracks_.Add(track);
                midiTrackEventsByIndex_[track.Index] = events;
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task AddTrackAssignmentAsync(MidiTrackAssignment assignment)
    {
        await StopMidiAsync(resetPosition: true);

        midiTrackAssignments_.RemoveAll(x => x.TrackIndex == assignment.TrackIndex);
        activeMidiPans_.RemoveAll(x => x.TrackIndex == assignment.TrackIndex);

        var assignedPan = BuildAssignedPan(assignment);
        if (assignedPan is null)
            return;

        midiTrackAssignments_.Add(assignment);
        activeMidiPans_.Add(assignedPan);

        RecalculatePlaybackDuration();
        playbackPosition_ = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        await InvokeAsync(StateHasChanged);
    }

    private async Task RemoveTrackAssignmentAsync(int trackIndex)
    {
        await StopMidiAsync(resetPosition: true);

        midiTrackAssignments_.RemoveAll(x => x.TrackIndex == trackIndex);

        var removedPans = activeMidiPans_
            .Where(x => x.TrackIndex == trackIndex)
            .ToList();

        foreach (var removedPan in removedPans)
            steelPanViews_.Remove(removedPan.InstanceId);

        activeMidiPans_.RemoveAll(x => x.TrackIndex == trackIndex);

        RecalculatePlaybackDuration();
        playbackPosition_ = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        await InvokeAsync(StateHasChanged);
    }

    private MidiAssignedPan? BuildAssignedPan(MidiTrackAssignment assignment)
    {
        if (assignment.AssignedPanType is null)
            return null;

        var sourcePan = pans_.FirstOrDefault(x => x.PanType == assignment.AssignedPanType.Value);
        if (sourcePan is null)
            return null;

        var rawEvents = midiTrackEventsByIndex_.GetValueOrDefault(assignment.TrackIndex) ?? [];
        var panInstance = ClonePan(sourcePan);
        var filteredEvents = PanMidiMapper.FilterToPan(panInstance, rawEvents);

        return new MidiAssignedPan
        {
            InstanceId = Guid.NewGuid(),
            TrackIndex = assignment.TrackIndex,
            TrackLabel = assignment.TrackLabel,
            PanType = assignment.AssignedPanType.Value,
            Pan = panInstance,
            Events = filteredEvents,
        };
    }

    private Task OnPanChanged(ChangeEventArgs e)
    {
        if (e.Value is not null && int.TryParse(e.Value.ToString(), out var selectedIndex))
            selectedPanIndex_ = selectedIndex;

        return InvokeAsync(StateHasChanged);
    }

    private Task OnPreviewSteelPanViewChanged(SteelPanView? view)
    {
        previewSteelPanView_ = view;
        return Task.CompletedTask;
    }

    private Task RegisterAssignedSteelPanViewAsync(Guid instanceId, SteelPanView? view)
    {
        if (view is null)
        {
            steelPanViews_.Remove(instanceId);
            return Task.CompletedTask;
        }

        steelPanViews_[instanceId] = view;
        return Task.CompletedTask;
    }

    private SteelPanView? GetInteractiveSteelPanView()
    {
        if (activeMidiPans_.Count > 0)
        {
            foreach (var assignedPan in activeMidiPans_)
            {
                if (steelPanViews_.TryGetValue(assignedPan.InstanceId, out var assignedView))
                    return assignedView;
            }
        }

        return previewSteelPanView_;
    }

    private async Task SelectChordAsync(HashSet<int> pitchClasses)
    {
        var view = GetInteractiveSteelPanView();
        if (view is null)
            return;

        await view.SelectChordAsync(pitchClasses);
    }

    private async Task PlaySelectedNotesAsync()
    {
        var view = GetInteractiveSteelPanView();
        if (view is null)
            return;

        await view.PlaySelectedNotesAsync();
    }

    private async Task ToggleToolsMenu()
    {
        showToolsMenu_ = !showToolsMenu_;
        await Task.CompletedTask;
    }

    private void OpenChordBuilderPanel()
    {
        showToolsMenu_ = false;
        showChordBuilderPanel_ = true;
        showMetronomePanel_ = false;
    }

    private void OpenMetronomePanel()
    {
        showToolsMenu_ = false;
        showMetronomePanel_ = true;
        showChordBuilderPanel_ = false;
    }

    private void CloseChordBuilderPanel()
    {
        showChordBuilderPanel_ = false;
    }

    private void CloseMetronomePanel()
    {
        showMetronomePanel_ = false;
    }

    private async Task ToggleMidiAsync()
    {
        if (isMidiPlaying_)
        {
            await PauseMidiAsync();
        }
        else
        {
            await PlayMidiAsync(playbackSessionStartOffset_);
        }
    }

    private async Task PlayMidiAsync(TimeSpan startOffset)
    {
        if (activeMidiPans_.Count == 0)
            return;

        var playbackGroups = activeMidiPans_
            .Select(x => new
            {
                Pan = x,
                Events = GetPlaybackEventsFromOffset(x.Events, startOffset)
            })
            .Where(x => x.Events.Count > 0)
            .ToList();

        if (playbackGroups.Count == 0)
        {
            playbackPosition_ = playbackDuration_;
            playbackSessionStartOffset_ = playbackDuration_;
            await InvokeAsync(StateHasChanged);
            return;
        }

        midiPlaybackCts_?.Cancel();
        midiPlaybackCts_?.Dispose();
        midiPlaybackCts_ = new CancellationTokenSource();

        playbackProgressCts_?.Cancel();
        playbackProgressCts_?.Dispose();
        playbackProgressCts_ = new CancellationTokenSource();

        if (metronomeEnabled_ && metronome_ is not null)
            await metronome_.StopAsync();

        isMidiPlaying_ = true;
        playbackSessionStartOffset_ = ClampPlaybackTime(startOffset);
        playbackPosition_ = playbackSessionStartOffset_;

        if (midiPlaybackInfo_ is not null)
        {
            metronomeBpm_ = EffectiveMidiBpm;
            metronomeBeatsPerBar_ = midiPlaybackInfo_.InitialBeatsPerBar;
            metronomeBeatUnit_ = midiPlaybackInfo_.InitialBeatUnit;
        }

        try
        {
            var firstGroup = playbackGroups[0];
            if (!steelPanViews_.TryGetValue(firstGroup.Pan.InstanceId, out var firstView))
                return;

            midiStartAt_ = await firstView.StartMidiSequenceAsync(firstGroup.Events, midiPlaybackCts_.Token);
            if (midiStartAt_ is null)
                return;

            foreach (var group in playbackGroups.Skip(1))
            {
                if (steelPanViews_.TryGetValue(group.Pan.InstanceId, out var view))
                    await view.StartMidiSequenceAtAsync(group.Events, midiStartAt_.Value, midiPlaybackCts_.Token);
            }

            if (metronomeEnabled_)
            {
                var metronomeActions = BuildMetronomeActions(
                    playbackGroups.SelectMany(x => x.Events).OrderBy(x => x.Start).ToList(),
                    metronomeBpm_,
                    metronomeBeatsPerBar_,
                    metronomeBeatUnit_,
                    playbackSessionStartOffset_);

                await JS.InvokeVoidAsync("steelPan.playMetronomeSchedule", metronomeActions, midiStartAt_.Value);
                await metronome_!.StartMidiVisualSyncAsync(midiStartAt_.Value, playbackSessionStartOffset_.TotalSeconds);
            }

            StartPlaybackProgressLoop(playbackSessionStartOffset_, playbackProgressCts_.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!midiPlaybackCts_.IsCancellationRequested)
            {
                isMidiPlaying_ = false;
                midiStartAt_ = null;
                playbackPosition_ = playbackDuration_;
                playbackSessionStartOffset_ = playbackDuration_;

                if (metronome_ is not null)
                    await metronome_.StopAsync();

                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task PauseMidiAsync()
    {
        playbackSessionStartOffset_ = await GetCurrentPlaybackPositionAsync();

        await StopMidiAsync(resetPosition: false);

        playbackPosition_ = playbackSessionStartOffset_;
        await InvokeAsync(StateHasChanged);
    }

    private async Task RestartMidiFromAsync(TimeSpan startOffset)
    {
        await StopMidiAsync(resetPosition: false);
        await PlayMidiAsync(startOffset);
    }

    private async Task StopMidiAsync(bool resetPosition = false)
    {
        midiPlaybackCts_?.Cancel();
        playbackProgressCts_?.Cancel();

        foreach (var steelPanView in steelPanViews_.Values)
            await steelPanView.StopMidiPlaybackAsync();

        if (metronome_ is not null)
            await metronome_.StopAsync();

        midiStartAt_ = null;
        isMidiPlaying_ = false;

        if (resetPosition)
        {
            playbackPosition_ = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;
        }

        await InvokeAsync(StateHasChanged);
    }

    private IReadOnlyList<MidiPanEvent> GetTempoAdjustedEvents(IReadOnlyList<MidiPanEvent> sourceEvents)
    {
        var sourceBpm = midiPlaybackInfo_?.InitialBpm ?? metronomeBpm_;
        var targetBpm = EffectiveMidiBpm;

        if (sourceBpm <= 0 || targetBpm <= 0 || sourceBpm == targetBpm)
            return sourceEvents;

        var scale = (double)sourceBpm / targetBpm;

        return sourceEvents
            .Select(e => new MidiPanEvent
            {
                Note = e.Note,
                Start = TimeSpan.FromTicks((long)(e.Start.Ticks * scale)),
                Duration = TimeSpan.FromTicks((long)(e.Duration.Ticks * scale)),
            })
            .ToList();
    }

    private IReadOnlyList<MidiPanEvent> GetPlaybackEventsFromOffset(
        IReadOnlyList<MidiPanEvent> sourceEvents,
        TimeSpan startOffset)
    {
        var playbackEvents = GetTempoAdjustedEvents(sourceEvents)
            .OrderBy(x => x.Start)
            .ToList();

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

    private List<MetronomeAction> BuildMetronomeActions(
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

        var remainingDurationSeconds = playbackEvents
            .Max(e => e.Start + e.Duration)
            .TotalSeconds;

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

    private Task OnMetronomeBpmChangedAsync(int bpm)
    {
        metronomeBpm_ = bpm;

        if (midiPlaybackInfo_ is not null)
        {
            midiBpmOverride_ = bpm;
            RecalculatePlaybackDuration();
        }

        return InvokeAsync(StateHasChanged);
    }

    private Task OnMetronomeBeatsPerBarChangedAsync(int beatsPerBar)
    {
        metronomeBeatsPerBar_ = beatsPerBar;
        return Task.CompletedTask;
    }

    private Task OnMetronomeBeatUnitChangedAsync(int beatUnit)
    {
        metronomeBeatUnit_ = beatUnit;
        return Task.CompletedTask;
    }

    private async Task OnMetronomeEnabledShellChangedAsync(ChangeEventArgs e)
    {
        var enabled = e.Value switch
        {
            bool value => value,
            string value when bool.TryParse(value, out var parsed) => parsed,
            _ => metronomeEnabled_
        };

        if (metronomeEnabled_ == enabled)
            return;

        await OnMetronomeEnabledChangedAsync(enabled);
    }

    private async Task OnMetronomeEnabledChangedAsync(bool enabled)
    {
        metronomeEnabled_ = enabled;

        if (!enabled)
        {
            if (metronome_ is not null)
                await metronome_.StopAsync();

            return;
        }

        if (!isMidiPlaying_ || midiStartAt_ is null)
            return;

        var absolutePosition = await GetCurrentPlaybackPositionAsync();
        var currentAudioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");

        var remainingEvents = activeMidiPans_
            .SelectMany(x => GetPlaybackEventsFromOffset(x.Events, absolutePosition))
            .OrderBy(x => x.Start)
            .ToList();

        var metronomeActions = BuildMetronomeActions(
            remainingEvents,
            metronomeBpm_,
            metronomeBeatsPerBar_,
            metronomeBeatUnit_,
            absolutePosition);

        await JS.InvokeVoidAsync("steelPan.playMetronomeSchedule", metronomeActions, currentAudioTime + 0.05);
        await metronome_!.StartMidiVisualSyncAsync(midiStartAt_.Value, absolutePosition.TotalSeconds);
    }

    private async Task SeekToStartAsync()
    {
        playbackPosition_ = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        if (isMidiPlaying_)
            await RestartMidiFromAsync(TimeSpan.Zero);
        else
            await InvokeAsync(StateHasChanged);
    }

    private async Task HandleNextTrackAsync()
    {
        if (isMidiPlaying_)
            await StopMidiAsync();

        playbackPosition_ = playbackDuration_;
        playbackSessionStartOffset_ = playbackDuration_;
        await InvokeAsync(StateHasChanged);
    }

    private Task HandleSeekPreviewChangedAsync(TimeSpan previewTime)
    {
        playbackPosition_ = ClampPlaybackTime(previewTime);
        return Task.CompletedTask;
    }

    private async Task HandleSeekCommittedAsync(TimeSpan seekTime)
    {
        var clamped = ClampPlaybackTime(seekTime);

        playbackPosition_ = clamped;
        playbackSessionStartOffset_ = clamped;

        if (isMidiPlaying_)
            await RestartMidiFromAsync(clamped);
        else
            await InvokeAsync(StateHasChanged);
    }

    private async Task<TimeSpan> GetCurrentPlaybackPositionAsync(TimeSpan? baseOffset = null)
    {
        if (!isMidiPlaying_ || midiStartAt_ is null)
            return ClampPlaybackTime(baseOffset ?? playbackSessionStartOffset_);

        var currentAudioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");
        var elapsedSeconds = Math.Max(0, currentAudioTime - midiStartAt_.Value);

        return ClampPlaybackTime((baseOffset ?? playbackSessionStartOffset_) + TimeSpan.FromSeconds(elapsedSeconds));
    }

    private TimeSpan ClampPlaybackTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            return TimeSpan.Zero;

        if (playbackDuration_ > TimeSpan.Zero && time > playbackDuration_)
            return playbackDuration_;

        return time;
    }

    private void StartPlaybackProgressLoop(TimeSpan baseOffset, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && isMidiPlaying_)
                {
                    var currentPosition = await GetCurrentPlaybackPositionAsync(baseOffset);
                    playbackPosition_ = currentPosition;

                    await InvokeAsync(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                            StateHasChanged();
                    });

                    await Task.Delay(25, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private void RecalculatePlaybackDuration()
    {
        var maxEnd = activeMidiPans_
            .SelectMany(x => GetTempoAdjustedEvents(x.Events))
            .DefaultIfEmpty()
            .Max(x => x is null ? TimeSpan.Zero : x.Start + x.Duration);

        playbackDuration_ = maxEnd;
        playbackPosition_ = ClampPlaybackTime(playbackPosition_);
        playbackSessionStartOffset_ = ClampPlaybackTime(playbackSessionStartOffset_);
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
}
