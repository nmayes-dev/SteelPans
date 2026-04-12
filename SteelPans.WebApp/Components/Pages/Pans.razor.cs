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
    private int selectedPanIndex_;
    private string? loadError_;

    private bool mergeAllTracks_ = false;
    private IBrowserFile? midiFile_;
    private List<MidiTrackOption> midiTrackOptions_ = [];
    private int? selectedTrackIndex_;

    private MidiPlaybackInfo? midiPlaybackInfo_;
    private List<MidiPanEvent> midiEvents_ = [];
    private CancellationTokenSource? midiPlaybackCts_;
    private bool isMidiPlaying_;
    private double? midiStartAt_;

    private int metronomeBpm_ = 120;
    private int metronomeBeatsPerBar_ = 4;
    private int metronomeBeatUnit_ = 4;
    private bool metronomeEnabled_ = false;
    private int? midiBpmOverride_;

    private TimeSpan playbackPosition_;
    private TimeSpan playbackDuration_;
    private CancellationTokenSource? playbackProgressCts_;
    private TimeSpan playbackSessionStartOffset_ = TimeSpan.Zero;
    private bool showChordBuilderPanel_;
    private bool showMetronomePanel_;

    private SteelPanView? steelPanView_;
    private Metronome? metronome_;

    private SteelPan? SelectedPan =>
        selectedPanIndex_ >= 0 && selectedPanIndex_ < pans_.Count
            ? pans_[selectedPanIndex_]
            : null;

    private int EffectiveMidiBpm =>
        midiBpmOverride_
            ?? midiPlaybackInfo_?.InitialBpm
            ?? metronomeBpm_;

    private sealed class MidiTrackOption
    {
        public required int Index { get; init; }
        public required string Label { get; init; }
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            pans_.Clear();
            pans_.AddRange(await PanLoader.LoadAsync());
            selectedPanIndex_ = 0;
        }
        catch (Exception ex)
        {
            loadError_ = ex.Message;
        }
    }

    private async Task OnMidiSelectedAsync(IBrowserFile e)
    {
        await StopMidiAsync();

        midiFile_ = e;

        midiTrackOptions_.Clear();
        midiEvents_.Clear();
        selectedTrackIndex_ = null;
        midiPlaybackInfo_ = null;
        midiBpmOverride_ = null;

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

        await using (var trackInfoStream = midiFile_.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024))
        {
            var trackInfos = await MidiLoader.GetTrackInfosAsync(trackInfoStream);

            midiTrackOptions_ = trackInfos
                .Where(t => t.NoteCount > 0)
                .Select(BuildTrackOption)
                .ToList();
        }

        selectedTrackIndex_ = midiTrackOptions_
            .FirstOrDefault(x => x.Index >= 0)?.Index ?? 0;

        await ReloadSelectedTrackAsync();
    }

    private async Task OnMergeAllTracksChangedAsync(bool mergeTracks)
    {
        mergeAllTracks_ = mergeTracks;
        await ReloadSelectedTrackAsync();
    }


    private async Task OnTrackChanged(ChangeEventArgs e)
    {
        if (e.Value is null || !int.TryParse(e.Value.ToString(), out var trackIndex))
            return;

        selectedTrackIndex_ = trackIndex;
        await ReloadSelectedTrackAsync();
    }

    private async Task OnPanChanged(ChangeEventArgs e)
    {
        if (e.Value is null || !int.TryParse(e.Value.ToString(), out var selectedIndex))
            return;

        selectedPanIndex_ = selectedIndex;
        await ReloadSelectedTrackAsync();
    }

    private async Task ReloadSelectedTrackAsync()
    {
        await StopMidiAsync();

        if (midiFile_ is null)
        {
            midiEvents_.Clear();
            playbackDuration_ = TimeSpan.Zero;
            playbackPosition_ = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await using var stream = midiFile_.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024);

        var rawEvents = mergeAllTracks_
            ? await MidiLoader.LoadMergedMidiAsync(stream)
            : await MidiLoader.LoadSingleTrackAsync(stream, selectedTrackIndex_ ?? 0);

        midiEvents_ = SelectedPan is not null
            ? PanMidiMapper.FilterToPan(SelectedPan, rawEvents)
            : rawEvents;

        playbackDuration_ = midiEvents_.Count == 0
            ? TimeSpan.Zero
            : midiEvents_.Max(e => e.Start + e.Duration);

        playbackPosition_ = TimeSpan.Zero;
        playbackSessionStartOffset_ = TimeSpan.Zero;

        await InvokeAsync(StateHasChanged);
    }

    private async Task SelectChordAsync(HashSet<int> pitchClasses)
    {
        if (steelPanView_ is null)
            return;

        await steelPanView_.SelectChordAsync(pitchClasses);
    }

    private async Task PlaySelectedNotesAsync()
    {
        if (steelPanView_ is null)
            return;

        await steelPanView_.PlaySelectedNotesAsync();
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
        if (steelPanView_ is null || midiEvents_.Count == 0)
            return;

        var playbackEvents = GetPlaybackEventsFromOffset(startOffset);

        if (playbackEvents.Count == 0)
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

        if (metronomeEnabled_)
            await metronome_!.StopAsync();

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
            midiStartAt_ = await steelPanView_.StartMidiSequenceAsync(
                playbackEvents,
                midiPlaybackCts_.Token);

            if (metronomeEnabled_ && midiStartAt_ is not null)
            {
                var metronomeActions = BuildMetronomeActions(
                    playbackEvents,
                    metronomeBpm_,
                    metronomeBeatsPerBar_,
                    metronomeBeatUnit_,
                    playbackSessionStartOffset_);

                await JS.InvokeVoidAsync(
                    "steelPan.playMetronomeSchedule",
                    metronomeActions,
                    midiStartAt_.Value);

                await metronome_!.StartMidiVisualSyncAsync(midiStartAt_.Value, playbackSessionStartOffset_.TotalSeconds);
            }

            StartPlaybackProgressLoop(playbackSessionStartOffset_, playbackProgressCts_.Token);

            await steelPanView_.MidiPlaybackCompletion;

            if (!midiPlaybackCts_.IsCancellationRequested)
            {
                playbackPosition_ = playbackDuration_;
                playbackSessionStartOffset_ = playbackDuration_;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PauseMidiAsync()
    {
        if (!isMidiPlaying_)
            return;

        playbackPosition_ = await GetCurrentPlaybackPositionAsync();
        playbackSessionStartOffset_ = playbackPosition_;

        await StopMidiAsync(resetPosition: false);
    }

    private async Task RestartMidiFromAsync(TimeSpan position)
    {
        await StopMidiAsync(resetPosition: false);
        await PlayMidiAsync(position);
    }

    private async Task StopMidiAsync(bool resetPosition = false)
    {
        if (!isMidiPlaying_)
            return;

        playbackProgressCts_?.Cancel();
        playbackProgressCts_?.Dispose();
        playbackProgressCts_ = null;

        midiPlaybackCts_?.Cancel();
        midiPlaybackCts_?.Dispose();
        midiPlaybackCts_ = null;

        if (steelPanView_ is not null)
            await steelPanView_.StopMidiPlaybackAsync();

        if (metronome_ is not null)
            await metronome_.StopAsync();

        midiStartAt_ = null;
        isMidiPlaying_ = false;

        if (resetPosition)
        {
            playbackPosition_ = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;
        }
    }

    private IReadOnlyList<MidiPanEvent> GetPlaybackEvents()
    {
        if (midiPlaybackInfo_ is null)
            return midiEvents_;

        var sourceBpm = midiPlaybackInfo_.InitialBpm;
        var targetBpm = EffectiveMidiBpm;

        if (sourceBpm <= 0 || targetBpm <= 0 || sourceBpm == targetBpm)
            return midiEvents_;

        var scale = (double)sourceBpm / targetBpm;

        return midiEvents_
            .Select(e => new MidiPanEvent
            {
                Note = e.Note,
                Start = TimeSpan.FromTicks((long)(e.Start.Ticks * scale)),
                Duration = TimeSpan.FromTicks((long)(e.Duration.Ticks * scale)),
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

        // If we are exactly on a beat boundary, tick immediately.
        // Otherwise wait until the next beat.
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

        if (!isMidiPlaying_ && midiPlaybackInfo_ is not null)
            midiBpmOverride_ = bpm;

        return Task.CompletedTask;
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
            await metronome_!.StopAsync();

        if (!enabled || !isMidiPlaying_ || midiStartAt_ is null)
            return;

        var currentAudioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");
        var elapsedSeconds = Math.Max(0, currentAudioTime - midiStartAt_.Value);

        var playbackEvents = GetPlaybackEventsFromOffset(TimeSpan.FromSeconds(elapsedSeconds));
        var metronomeActions = BuildMetronomeActions(
            playbackEvents,
            metronomeBpm_,
            metronomeBeatsPerBar_,
            metronomeBeatUnit_,
            TimeSpan.FromSeconds(elapsedSeconds));

        var remainingActions = metronomeActions
            .Where(a => a.TimeSeconds >= elapsedSeconds)
            .Select(a => new MetronomeAction
            {
                TimeSeconds = a.TimeSeconds - elapsedSeconds,
                IsAccent = a.IsAccent,
            })
            .ToList();

        await JS.InvokeVoidAsync("steelPan.playMetronomeSchedule", remainingActions, null);

        await metronome_!.StartMidiVisualSyncAsync(midiStartAt_.Value, elapsedSeconds);
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

    private IReadOnlyList<MidiPanEvent> GetPlaybackEventsFromOffset(TimeSpan startOffset)
    {
        var playbackEvents = GetPlaybackEvents();
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
            .OrderBy(e => e.Start)
            .ToList();
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
                        if (cancellationToken.IsCancellationRequested)
                            return;

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

    private static MidiTrackOption BuildTrackOption(MidiTrackInfo track)
    {
        var name = string.IsNullOrWhiteSpace(track.Name)
            ? $"Track {track.Index + 1}"
            : $"Track {track.Index + 1}: {track.Name}";

        return new MidiTrackOption
        {
            Index = track.Index,
            Label = $"{name} ({track.NoteCount} notes)"
        };
    }
}
