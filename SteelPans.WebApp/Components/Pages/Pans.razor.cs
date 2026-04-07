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

    private IBrowserFile? midiFile_;
    private string? midiFileName_;
    private List<MidiTrackPlaceholderOption> midiTrackOptions_ = [];
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
    private bool suppressPlaybackReset_;
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

    private sealed class MetronomeAction
    {
        public required double TimeSeconds { get; init; }
        public required bool IsAccent { get; init; }
    }

    private sealed class MidiTrackPlaceholderOption
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

    private async Task OnMidiSelected(InputFileChangeEventArgs e)
    {
        if (e.FileCount == 0)
            return;

        await StopMidiAsync();

        midiFile_ = e.File;
        midiFileName_ = midiFile_.Name;

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

        midiTrackOptions_ =
        [
            new MidiTrackPlaceholderOption
            {
                Index = 0,
                Label = "All tracks merged"
            },
            new MidiTrackPlaceholderOption
            {
                Index = 1,
                Label = "Track selection unavailable"
            }
        ];

        selectedTrackIndex_ = midiTrackOptions_[0].Index;

        await ReloadSelectedTrackAsync();
    }

    private async Task OnTrackChanged(ChangeEventArgs e)
    {
        if (e.Value is null || !int.TryParse(e.Value.ToString(), out var trackIndex))
            return;

        selectedTrackIndex_ = trackIndex;

        // Placeholder for when per-track selection comes back.
        // For now all MIDI is always loaded as a merged sequence.
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

        var rawEvents = await MidiLoader.LoadMergedMidiAsync(stream);

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

    private void ToggleChordBuilderPanel()
    {
        showChordBuilderPanel_ = !showChordBuilderPanel_;
        showMetronomePanel_ = false;
    }

    private void CloseChordBuilderPanel()
    {
        showChordBuilderPanel_ = false;
    }

    private void ToggleMetronomePanel()
    {
        showMetronomePanel_ = !showMetronomePanel_;
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

            StartPlaybackProgressLoop(playbackSessionStartOffset_, playbackProgressCts_.Token);

            if (metronomeEnabled_ && midiStartAt_ is not null)
            {
                var metronomeActions = BuildMetronomeActions(
                    playbackEvents,
                    metronomeBpm_,
                    metronomeBeatsPerBar_,
                    metronomeBeatUnit_);

                await JS.InvokeVoidAsync(
                    "steelPan.playMetronomeSchedule",
                    metronomeActions,
                    midiStartAt_.Value);

                await metronome_!.StartMidiVisualSyncAsync(midiStartAt_.Value);
            }

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
        finally
        {
            playbackProgressCts_?.Cancel();
            playbackProgressCts_?.Dispose();
            playbackProgressCts_ = null;

            isMidiPlaying_ = false;
            midiStartAt_ = null;

            await metronome_!.StopAsync();

            if (!suppressPlaybackReset_)
                await InvokeAsync(StateHasChanged);
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
        suppressPlaybackReset_ = true;

        try
        {
            await StopMidiAsync(resetPosition: false);
            await PlayMidiAsync(position);
        }
        finally
        {
            suppressPlaybackReset_ = false;
        }
    }

    private async Task StopMidiAsync(bool resetPosition = false)
    {
        playbackProgressCts_?.Cancel();
        playbackProgressCts_?.Dispose();
        playbackProgressCts_ = null;

        if (midiPlaybackCts_ is not null)
        {
            midiPlaybackCts_.Cancel();
            midiPlaybackCts_.Dispose();
            midiPlaybackCts_ = null;
        }

        midiStartAt_ = null;

        await JS.InvokeVoidAsync("steelPan.stopMidiSchedule");
        await JS.InvokeVoidAsync("steelPan.stopMetronome");

        isMidiPlaying_ = false;

        if (steelPanView_ is not null)
            await steelPanView_.StopMidiPlaybackAsync();

        if (metronome_ is not null)
            await metronome_.StopAsync();

        if (resetPosition)
        {
            playbackPosition_ = TimeSpan.Zero;
            playbackSessionStartOffset_ = TimeSpan.Zero;
        }

        await InvokeAsync(StateHasChanged);
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
        int beatUnit)
    {
        var actions = new List<MetronomeAction>();

        if (playbackEvents.Count == 0 || bpm <= 0 || beatsPerBar <= 0 || beatUnit <= 0)
            return actions;

        var endTime = playbackEvents
            .Max(e => e.Start + e.Duration);

        var secondsPerBeat = (60.0 / bpm) * (4.0 / beatUnit);
        var totalBeats = (int)Math.Ceiling(endTime.TotalSeconds / secondsPerBeat);

        for (var i = 0; i < totalBeats; i++)
        {
            actions.Add(new MetronomeAction
            {
                TimeSeconds = i * secondsPerBeat,
                IsAccent = i % beatsPerBar == 0,
            });
        }

        return actions;
    }

    private Task OnMetronomeBpmChanged(int bpm)
    {
        metronomeBpm_ = bpm;

        if (!isMidiPlaying_ && midiPlaybackInfo_ is not null)
            midiBpmOverride_ = bpm;

        return Task.CompletedTask;
    }

    private Task OnMetronomeBeatsPerBarChanged(int beatsPerBar)
    {
        metronomeBeatsPerBar_ = beatsPerBar;
        return Task.CompletedTask;
    }

    private Task OnMetronomeBeatUnitChanged(int beatUnit)
    {
        metronomeBeatUnit_ = beatUnit;
        return Task.CompletedTask;
    }

    private async Task OnMetronomeEnabledChanged(bool enabled)
    {
        metronomeEnabled_ = enabled;

        await JS.InvokeVoidAsync("steelPan.stopMetronome");

        if (!enabled)
            await metronome_!.StopAsync();

        if (!enabled || !isMidiPlaying_ || midiStartAt_ is null)
            return;

        var currentAudioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");
        var elapsedSeconds = Math.Max(0, currentAudioTime - midiStartAt_.Value);

        var playbackEvents = GetPlaybackEvents();
        var metronomeActions = BuildMetronomeActions(
            playbackEvents,
            metronomeBpm_,
            metronomeBeatsPerBar_,
            metronomeBeatUnit_);

        var remainingActions = metronomeActions
            .Where(a => a.TimeSeconds >= elapsedSeconds)
            .Select(a => new MetronomeAction
            {
                TimeSeconds = a.TimeSeconds - elapsedSeconds,
                IsAccent = a.IsAccent,
            })
            .ToList();

        await JS.InvokeVoidAsync("steelPan.playMetronomeSchedule", remainingActions, null);

        if (metronome_ is not null)
            await metronome_.StartMidiVisualSyncAsync(midiStartAt_.Value);
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

                    await InvokeAsync(StateHasChanged);
                    await Task.Delay(25, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }
}
