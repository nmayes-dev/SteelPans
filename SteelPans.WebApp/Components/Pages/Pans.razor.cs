using Melanchall.DryWetMidi.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using SteelPans.WebApp.Components.Elements;
using SteelPans.WebApp.Components.Layout;
using SteelPans.WebApp.Model;
using SteelPans.WebApp.Services;
using System.Xml.Linq;

namespace SteelPans.WebApp.Components.Pages;

public partial class Pans
{
    private readonly List<SteelPan> pans_ = [];
    private readonly List<PanType> availablePanTypes_ = [];
    private readonly List<MidiTrackInfo> midiTracks_ = [];
    private readonly Dictionary<int, List<MidiPanEvent>> midiTrackEventsByIndex_ = [];
    private readonly Dictionary<Guid, SteelPanView> steelPanViews_ = [];

    private IReadOnlyList<PanType> AvailablePanTypes => availablePanTypes_.Where(x => !activeMidiPans_.Select(y => y.MidiPan.Pan.PanType).Contains(x)).ToList();

    private string? loadError_;

    private string midiFileName_ = string.Empty;

    private string mergeMidiFileName_ = string.Empty;
    private IReadOnlyList<IBrowserFile> pendingMergeMidiFiles_ = [];

    private MidiPlaybackInfo? midiPlaybackInfo_;
    private List<MidiTrackAssignment> midiTrackAssignments_ = [];

    private class PanWithToolbar
    {
        public required MidiAssignedPan MidiPan { get; set; }
        public Toolbar? Toolbar { get; set; }
    }

    private List<PanWithToolbar> activeMidiPans_ = [];

    private CancellationTokenSource? midiPlaybackCts_;
    private CancellationTokenSource? playbackProgressCts_;

    private bool isMidiPlaying_;
    private double? midiStartAt_;

    private int metronomeBpm_ = 120;
    private int metronomeBeatsPerBar_ = 4;
    private int metronomeBeatUnit_ = 4;
    private bool metronomeEnabled_;
    private int? midiBpmOverride_;

    private Guid? pendingRestartPanInstanceId_;
    private TimeSpan? pendingRestartOffset_;

    private TimeSpan playbackPosition_;
    private TimeSpan playbackDuration_;
    private TimeSpan playbackSessionStartOffset_ = TimeSpan.Zero;
    private double? playbackAudioAnchorTime_;
    private TimeSpan playbackScoreAnchorOffset_ = TimeSpan.Zero;
    private int playbackTempoAnchorBpm_ = 120;

    private Metronome? metronome_;
    private AddPanModal? addPanModal_;
    private ModalPopup? addMergedTrackModal_;

    private int InitialMidiBpm => midiPlaybackInfo_?.InitialBpm ?? metronomeBpm_;
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
        }
        catch (Exception ex)
        {
            loadError_ = ex.Message;
        }
    }

    private async Task OnMidiFileSelected(Func<Task<MidiFile>> getMidiFile)
    {
        await StopMidiAsync(resetPosition: true);

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
        playbackAudioAnchorTime_ = null;
        playbackScoreAnchorOffset_ = TimeSpan.Zero;
        playbackTempoAnchorBpm_ = 120;

        var midiFile = await getMidiFile();

        midiPlaybackInfo_ = MidiService.GetPlaybackInfo(midiFile);

        if (midiPlaybackInfo_ is not null)
        {
            metronomeBpm_ = midiPlaybackInfo_.InitialBpm;
            metronomeBeatsPerBar_ = midiPlaybackInfo_.InitialBeatsPerBar;
            metronomeBeatUnit_ = midiPlaybackInfo_.InitialBeatUnit;
        }

        var playableTracks = MidiService.LoadPlayableTracks(midiFile);
        foreach (var (track, events) in playableTracks)
        {
            midiTracks_.Add(track);
            midiTrackEventsByIndex_[track.Index] = events;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task CloseMergeMidiModal()
    {
        mergeMidiFileName_ = string.Empty;
        pendingMergeMidiFiles_ = [];
    }

    private async Task ConfirmMergeMidiAsync()
    {
        if (pendingMergeMidiFiles_.Count == 0 || addMergedTrackModal_ is null)
            return;

        midiFileName_ = $"{mergeMidiFileName_.Trim()}.mid";

        await OnMidiFileSelected(() => MidiService.MergeMidiTracksAsync(midiFileName_, pendingMergeMidiFiles_));
        await addMergedTrackModal_.RequestCloseAsync();

        pendingMergeMidiFiles_ = [];
        mergeMidiFileName_ = string.Empty;
    }

    private async Task OnMultipleMidiSelectedAsync(IReadOnlyList<IBrowserFile> files)
    {
        if (addMergedTrackModal_ is null)
            return;

        pendingMergeMidiFiles_ = files;
        await addMergedTrackModal_.OpenModal();
    }

    private async Task OnSingleMidiSelectedAsync(IBrowserFile file)
    {
        midiFileName_ = file.Name;
        await OnMidiFileSelected(() => MidiService.OpenMidiFileAsync(file));
    }

    private async Task AddTrackAssignmentAsync(MidiTrackAssignment assignment)
    {
        var restartOffset = isMidiPlaying_
            ? await GetCurrentPlaybackPositionAsync()
            : (TimeSpan?)null;

        midiTrackAssignments_.RemoveAll(x => x.TrackIndex == assignment.TrackIndex);
        activeMidiPans_.RemoveAll(x => x.MidiPan.TrackIndex == assignment.TrackIndex);

        var assignedPan = BuildAssignedPan(assignment);
        if (assignedPan is null)
            return;

        midiTrackAssignments_.Add(assignment);
        activeMidiPans_.Add(new PanWithToolbar { MidiPan = assignedPan });

        RecalculatePlaybackDuration();

        if (restartOffset is not null)
        {
            pendingRestartPanInstanceId_ = assignedPan.InstanceId;
            pendingRestartOffset_ = restartOffset.Value;
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task RemoveTrackAssignmentAsync(int trackIndex)
    {
        midiTrackAssignments_.RemoveAll(x => x.TrackIndex == trackIndex);

        if (!midiTrackAssignments_.Any())
            await StopMidiAsync();

        var removedPans = activeMidiPans_
            .Where(x => x.MidiPan.TrackIndex == trackIndex)
            .ToList();

        foreach (var removedPan in removedPans)
        {
            steelPanViews_.Remove(removedPan.MidiPan.InstanceId);
            removedPan.Toolbar?.Close();
        }

        activeMidiPans_.RemoveAll(x => x.MidiPan.TrackIndex == trackIndex);

        RecalculatePlaybackDuration();
        playbackPosition_ = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        await InvokeAsync(StateHasChanged);
    }

    private MidiAssignedPan? BuildAssignedPan(MidiTrackAssignment assignment)
    {
        var sourcePan = pans_.FirstOrDefault(x => x.PanType == assignment.AssignedPanType);
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
            PanType = assignment.AssignedPanType,
            Pan = panInstance,
            Events = filteredEvents,
        };
    }

    private async Task RegisterAssignedSteelPanViewAsync(Guid instanceId, SteelPanView? view)
    {
        if (view is null)
        {
            steelPanViews_.Remove(instanceId);
            return;
        }

        steelPanViews_[instanceId] = view;

        var assignedPan = activeMidiPans_.FirstOrDefault(x => x.MidiPan.InstanceId == instanceId);
        if (assignedPan is null)
            return;

        await JS.InvokeVoidAsync("steelPan.setComponentVolume", view.ComponentId, assignedPan.MidiPan.Volume);

        if (!isMidiPlaying_)
            return;

        if (pendingRestartPanInstanceId_ == instanceId && pendingRestartOffset_ is not null)
        {
            var restartOffset = pendingRestartOffset_.Value;
            pendingRestartPanInstanceId_ = null;
            pendingRestartOffset_ = null;

            await RestartMidiFromAsync(restartOffset);
            return;
        }

        var currentPosition = await GetCurrentPlaybackPositionAsync();
        var playbackEvents = GetPlaybackEventsFromOffset(assignedPan.MidiPan.Events, currentPosition);

        if (playbackEvents.Count == 0)
            return;

        var currentAudioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");

        await view.StartMidiSequenceAtAsync(
            playbackEvents,
            currentAudioTime + 0.05,
            InitialMidiBpm,
            EffectiveMidiBpm,
            midiPlaybackCts_?.Token ?? CancellationToken.None);
    }

    private async Task OnPanVolumeChangedAsync(MidiAssignedPan activePan, double volume)
    {
        activePan.Volume = Math.Clamp(volume, 0.0, 1.0);
        if (steelPanViews_.TryGetValue(activePan.InstanceId, out var view))
            await JS.InvokeVoidAsync("steelPan.setComponentVolume", view.ComponentId, activePan.Volume);
    }

    private SteelPanView? GetInteractiveSteelPanView()
    {
        if (activeMidiPans_.Count > 0)
        {
            foreach (var assignedPan in activeMidiPans_)
            {
                if (steelPanViews_.TryGetValue(assignedPan.MidiPan.InstanceId, out var assignedView))
                    return assignedView;
            }
        }

        return null;
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
                Pan = x.MidiPan,
                Events = GetPlaybackEventsFromOffset(x.MidiPan.Events, startOffset)
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

            midiStartAt_ = await firstView.StartMidiSequenceAsync(firstGroup.Events, InitialMidiBpm, EffectiveMidiBpm, midiPlaybackCts_.Token);
            if (midiStartAt_ is null)
                return;

            playbackAudioAnchorTime_ = midiStartAt_.Value;
            playbackScoreAnchorOffset_ = playbackSessionStartOffset_;
            playbackTempoAnchorBpm_ = EffectiveMidiBpm;

            foreach (var group in playbackGroups.Skip(1))
            {
                if (steelPanViews_.TryGetValue(group.Pan.InstanceId, out var view))
                    await view.StartMidiSequenceAtAsync(group.Events, midiStartAt_.Value, InitialMidiBpm, EffectiveMidiBpm, midiPlaybackCts_.Token);
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
        playbackAudioAnchorTime_ = null;
        playbackTempoAnchorBpm_ = EffectiveMidiBpm;

        if (resetPosition)
        {
            playbackPosition_ = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;
        }

        await InvokeAsync(StateHasChanged);
    }

    private double GetPlaybackTempoRatio(int bpm)
    {
        var sourceBpm = midiPlaybackInfo_?.InitialBpm ?? bpm;
        if (sourceBpm <= 0 || bpm <= 0)
            return 1.0;

        return (double)bpm / sourceBpm;
    }

    private IReadOnlyList<MidiPanEvent> GetPlaybackEventsFromOffset(
        IReadOnlyList<MidiPanEvent> sourceEvents,
        TimeSpan startOffset)
    {
        var playbackEvents = sourceEvents
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

    private async Task OnMetronomeBpmChangedAsync(int bpm)
    {
        metronomeBpm_ = bpm;

        if (midiPlaybackInfo_ is not null)
            midiBpmOverride_ = bpm;

        if (isMidiPlaying_)
        {
            var currentPosition = await GetCurrentPlaybackPositionAsync();
            var currentAudioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");

            playbackScoreAnchorOffset_ = currentPosition;
            playbackAudioAnchorTime_ = currentAudioTime;
            playbackTempoAnchorBpm_ = bpm;

            foreach (var steelPanView in steelPanViews_.Values)
                await JS.InvokeVoidAsync("steelPan.updateMidiTempo", steelPanView.ComponentId, bpm);
        }

        await InvokeAsync(StateHasChanged);
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
            .SelectMany(x => GetPlaybackEventsFromOffset(x.MidiPan.Events, absolutePosition))
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
        if (!isMidiPlaying_ || playbackAudioAnchorTime_ is null)
            return ClampPlaybackTime(baseOffset ?? playbackSessionStartOffset_);

        var currentAudioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");
        var elapsedAudioSeconds = Math.Max(0, currentAudioTime - playbackAudioAnchorTime_.Value);

        var ratio = GetPlaybackTempoRatio(playbackTempoAnchorBpm_);
        var elapsedScoreSeconds = elapsedAudioSeconds * ratio;

        return ClampPlaybackTime(playbackScoreAnchorOffset_ + TimeSpan.FromSeconds(elapsedScoreSeconds));
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
            .SelectMany(x => x.MidiPan.Events)
            .DefaultIfEmpty()
            .Max(x => x is null ? TimeSpan.Zero : x.Start + x.Duration);

        playbackDuration_ = maxEnd;
        playbackPosition_ = ClampPlaybackTime(playbackPosition_);
        playbackSessionStartOffset_ = ClampPlaybackTime(playbackSessionStartOffset_);
        playbackScoreAnchorOffset_ = ClampPlaybackTime(playbackScoreAnchorOffset_);
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
