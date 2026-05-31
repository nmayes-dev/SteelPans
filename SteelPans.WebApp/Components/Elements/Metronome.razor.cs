using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SteelPans.WebApp.Services;

namespace SteelPans.WebApp.Components.Elements;

public partial class Metronome
{
    [Inject] private MidiPlaybackService Playback { get; set; } = default!;

    private bool MidiLoaded => Playback.ActivePans.Count > 0;
    private double? MidiStartAt => midiStartAt_ ?? Playback.MidiStartAt;
    private int Bpm => Playback.TempoBpm;
    private int BeatsPerBar => Playback.BeatsPerBar;
    private int BeatUnit => Playback.BeatUnit;
    private bool Enabled => Playback.ClickTrackEnabled;

    private const int MinBpm = 20;
    private const int MaxBpm = 200;

    // Original design values converted to track-relative ratios.
    // Base design track height was 167px, with the draggable weight 21px tall.
    private const double DesignTrackHeightPx = 167.0;
    private const double WeightHeightRatio = 21.0 / DesignTrackHeightPx;

    private const double WeightMinTopRatio = 0.0;
    private const double WeightMaxTopRatio = 146.0 / DesignTrackHeightPx;

    private const double LargoTopRatio = 14.0 / DesignTrackHeightPx;
    private const double AndanteTopRatio = 64.0 / DesignTrackHeightPx;
    private const double AllegroTopRatio = 116.0 / DesignTrackHeightPx;

    private const int LargoBpm = 52;
    private const int AndanteBpm = 92;
    private const int AllegroBpm = 144;

    private const double MaxArmAngleDeg = 24.0;

    private const int BpmRepeatInitialDelayMs = 350;
    private const int BpmRepeatIntervalMs = 50;

    private CancellationTokenSource? bpmRepeatCts_;

    private readonly string componentId_ = $"metronome_{Guid.NewGuid():N}";
    private CancellationTokenSource? loopCts_;
    private CancellationTokenSource? midiVisualCts_;
    private DotNetObjectReference<Metronome>? selfRef_;
    private ElementReference weightTrackRef_;

    private int beatIndex_;
    private int displayBeat_ = 1;
    private int? lastFlashedBeatIndex_;
    private bool jsAvailable_;
    private double armAngleDeg_;

    private double midiVisualStartOffsetSeconds_;
    private double? midiStartAt_;
    private double? midiVisualAudioAnchorTime_;
    private double midiVisualScoreAnchorSeconds_;
    private int midiVisualTempoAnchorBpm_ = 120;

    public enum PlayState
    {
        NotPlaying,
        Manual,
        MIDI
    }

    private PlayState playState_ = PlayState.NotPlaying;
    public bool IsPlaying => playState_ is PlayState.Manual or PlayState.MIDI;

    private bool tickFlash_;
    private bool accentFlash_;

    private bool EffectiveIsPlaying => Enabled && IsPlaying;

    private bool CanEditBpm => Enabled || MidiLoaded;
    private bool CanEditTimeSignature => Enabled && !IsPlaying && !MidiLoaded;

    private double ArmAngleDeg => !Enabled || !EffectiveIsPlaying
        ? 0.0
        : armAngleDeg_;

    private double WeightTopPercent => GetWeightTopRatio(Bpm) * 100.0;

    protected override void OnInitialized()
    {
        Playback.StateChanged += OnPlaybackStateChangedAsync;
        Playback.PlaybackStarted += OnPlaybackStartedAsync;
        Playback.PlaybackPaused += OnPlaybackPausedAsync;
        Playback.PlaybackStopped += OnPlaybackStoppedAsync;
        Playback.TempoChanged += OnTempoChangedAsync;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            selfRef_ = DotNetObjectReference.Create(this);
            jsAvailable_ = true;
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    private async Task OnPlaybackStateChangedAsync()
    {
        if (!Enabled && IsPlaying)
            await StopAsync();

        await InvokeAsync(StateHasChanged);
    }

    private async Task OnPlaybackStartedAsync(MidiPlaybackStartedEventArgs args)
    {
        await StopAsync(resetVisuals: false);

        if (!Enabled || !jsAvailable_)
        {
            playState_ = PlayState.NotPlaying;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var clickTrackActions = MidiPlaybackService.BuildClickTrackActions(
            args.PlaybackEvents,
            Bpm,
            BeatsPerBar,
            BeatUnit,
            args.StartOffset);

        await JS.InvokeVoidAsync("steelPan.playMetronomeSchedule", clickTrackActions, args.StartAt);
        await StartMidiVisualSyncAsync(args.StartAt, args.StartOffset.TotalSeconds);
    }

    private async Task OnPlaybackPausedAsync(MidiPlaybackPausedEventArgs args)
    {
        if (playState_ == PlayState.MIDI)
            await StopMidiVisualSyncAsync(resetVisuals: false);

        playState_ = PlayState.NotPlaying;
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnPlaybackStoppedAsync(MidiPlaybackStoppedEventArgs args)
    {
        if (playState_ == PlayState.MIDI)
            await StopMidiVisualSyncAsync(resetVisuals: args.ResetPosition);

        playState_ = PlayState.NotPlaying;
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnTempoChangedAsync(PlaybackTempoChangedEventArgs args)
    {
        await UpdateMidiVisualTempoAsync(args.Bpm);
        await InvokeAsync(StateHasChanged);
    }

    private async Task BeginWeightDragAsync(PointerEventArgs e)
    {
        if (selfRef_ is null || IsPlaying)
            return;

        await JS.InvokeVoidAsync("steelPan.beginMetronomeWeightDrag", weightTrackRef_, selfRef_, e.ClientY);
    }

    [JSInvokable]
    public async Task OnMetronomeWeightDragged(double localPointerY, double trackHeightPx)
    {
        var bpm = GetBpmFromPointerY(localPointerY, trackHeightPx);
        await SetBpmAsync(bpm);
    }

    private async Task StopAsync(bool resetVisuals = true)
    {
        if (playState_ == PlayState.Manual)
            await StopLoopAsync();
        else if (playState_ == PlayState.MIDI)
            await StopMidiVisualSyncAsync(resetVisuals: resetVisuals);

        playState_ = PlayState.NotPlaying;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleManualPlayAsync()
    {
        if (!Enabled || Playback.IsPlaying || playState_ == PlayState.MIDI)
            return;

        if (playState_ == PlayState.Manual)
        {
            await StopAsync();
            return;
        }

        await StartLoopAsync();
    }

    private async Task IncrementBpmAsync()
    {
        await SetBpmAsync(Bpm + 1);
    }

    private async Task DecrementBpmAsync()
    {
        await SetBpmAsync(Bpm - 1);
    }

    private async Task BeginBpmRepeatAsync(int delta)
    {
        StopBpmRepeat();

        await ChangeBpmAsync(delta);

        bpmRepeatCts_ = new CancellationTokenSource();
        var token = bpmRepeatCts_.Token;

        _ = RepeatBpmChangeAsync(delta, token);
    }

    private void StopBpmRepeat()
    {
        if (bpmRepeatCts_ is null)
            return;

        bpmRepeatCts_.Cancel();
        bpmRepeatCts_.Dispose();
        bpmRepeatCts_ = null;
    }

    private async Task RepeatBpmChangeAsync(int delta, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(BpmRepeatInitialDelayMs, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                await ChangeBpmAsync(delta);
                await Task.Delay(BpmRepeatIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ChangeBpmAsync(int delta)
    {
        var nextBpm = Math.Clamp(Bpm + delta, MinBpm, MaxBpm);

        if (nextBpm == Bpm)
        {
            StopBpmRepeat();
            return;
        }

        await SetBpmAsync(nextBpm);
    }

    private async Task SetBpmAsync(int bpm)
    {
        bpm = Math.Clamp(bpm, MinBpm, MaxBpm);

        if (Bpm == bpm)
            return;

        await Playback.SetTempoBpmAsync(bpm);

        if (Enabled && playState_ == PlayState.Manual)
            await RestartLoopAsync();
        else
            await InvokeAsync(StateHasChanged);
    }

    private async Task IncrementBeatsPerBarAsync()
    {
        await SetBeatsPerBarAsync(BeatsPerBar + 1);
    }

    private async Task DecrementBeatsPerBarAsync()
    {
        await SetBeatsPerBarAsync(BeatsPerBar - 1);
    }

    private async Task SetBeatsPerBarAsync(int beatsPerBar)
    {
        beatsPerBar = Math.Clamp(beatsPerBar, 1, 32);

        if (BeatsPerBar == beatsPerBar)
            return;

        await Playback.SetBeatsPerBarAsync(beatsPerBar);

        if (beatIndex_ >= BeatsPerBar)
            beatIndex_ = 0;

        displayBeat_ = Math.Clamp(displayBeat_, 1, BeatsPerBar);

        if (Enabled && playState_ == PlayState.Manual)
            await RestartLoopAsync();
        else
            await InvokeAsync(StateHasChanged);
    }

    private async Task OnBeatUnitChanged(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out var beatUnit))
            return;

        if (beatUnit is not (1 or 2 or 4 or 8 or 16 or 32))
            return;

        if (BeatUnit == beatUnit)
            return;

        await Playback.SetBeatUnitAsync(beatUnit);

        if (Enabled && playState_ == PlayState.Manual)
            await RestartLoopAsync();
        else
            await InvokeAsync(StateHasChanged);
    }

    private async Task RestartLoopAsync()
    {
        await StopLoopAsync();
        await StartLoopAsync();
    }

    private async Task StartLoopAsync()
    {
        if (!Enabled || Playback.IsPlaying)
            return;

        await StopLoopAsync();

        playState_ = PlayState.Manual;
        loopCts_ = new CancellationTokenSource();
        var token = loopCts_.Token;

        beatIndex_ = 0;
        displayBeat_ = 1;
        armAngleDeg_ = 0.0;
        tickFlash_ = false;
        accentFlash_ = false;
        lastFlashedBeatIndex_ = null;

        _ = RunLoopAsync(token);

        await InvokeAsync(StateHasChanged);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var bpm = Math.Clamp(Bpm, MinBpm, MaxBpm);
            var beatsPerBar = Math.Max(1, BeatsPerBar);
            var beatUnit = BeatUnit > 0 ? BeatUnit : 4;
            var interval = TimeSpan.FromMinutes((4.0 / beatUnit) / bpm);

            var nextBeatTime = TimeSpan.Zero;

            while (!cancellationToken.IsCancellationRequested)
            {
                var now = stopwatch.Elapsed;

                while (now >= nextBeatTime && !cancellationToken.IsCancellationRequested)
                {
                    var isAccent = beatIndex_ % beatsPerBar == 0;
                    displayBeat_ = (beatIndex_ % beatsPerBar) + 1;

                    tickFlash_ = true;
                    accentFlash_ = isAccent;

                    await InvokeAsync(StateHasChanged);
                    await JS.InvokeVoidAsync("steelPan.playMetronomeTick", isAccent);

                    _ = ClearFlashSoonAsync();

                    beatIndex_++;
                    nextBeatTime += interval;
                }

                var elapsedBeats = interval > TimeSpan.Zero
                    ? now.TotalSeconds / interval.TotalSeconds
                    : 0.0;

                armAngleDeg_ = Math.Sin(elapsedBeats * Math.PI) * MaxArmAngleDeg;

                await InvokeAsync(StateHasChanged);
                await Task.Delay(16, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task StartMidiVisualSyncAsync(double midiStartAt, double startOffsetSeconds = 0.0)
    {
        playState_ = PlayState.MIDI;

        if (!Enabled || !jsAvailable_)
            return;

        midiStartAt_ = midiStartAt;
        midiVisualStartOffsetSeconds_ = Math.Max(0.0, startOffsetSeconds);

        midiVisualAudioAnchorTime_ = midiStartAt;
        midiVisualScoreAnchorSeconds_ = Math.Max(0.0, startOffsetSeconds);
        midiVisualTempoAnchorBpm_ = Math.Clamp(Bpm, MinBpm, MaxBpm);

        midiVisualCts_?.Cancel();
        midiVisualCts_?.Dispose();
        midiVisualCts_ = new CancellationTokenSource();

        lastFlashedBeatIndex_ = null;

        _ = RunMidiVisualSyncAsync(midiVisualCts_.Token);

        await InvokeAsync(StateHasChanged);
    }

    private async Task RunMidiVisualSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!Enabled || playState_ != PlayState.MIDI || MidiStartAt is null)
                    break;

                var bpm = Math.Clamp(Bpm, MinBpm, MaxBpm);
                var beatsPerBar = Math.Max(1, BeatsPerBar);
                var beatUnit = BeatUnit > 0 ? BeatUnit : 4;
                var secondsPerBeat = (60.0 / bpm) * (4.0 / beatUnit);

                if (secondsPerBeat <= 0.0)
                    break;

                double audioTime;

                try
                {
                    audioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");
                }
                catch
                {
                    break;
                }

                var elapsedSeconds = midiVisualScoreAnchorSeconds_
                    + Math.Max(0.0, audioTime - (midiVisualAudioAnchorTime_ ?? MidiStartAt.Value));

                var elapsedBeats = elapsedSeconds / secondsPerBeat;

                var totalBeatIndex = (int)Math.Floor(elapsedBeats);
                var beatInBar = Mod(totalBeatIndex, beatsPerBar);
                var beatPhase = elapsedBeats - totalBeatIndex;

                displayBeat_ = beatInBar + 1;
                beatIndex_ = totalBeatIndex;

                var isAccent = beatInBar == 0;

                var shouldFlash = beatPhase <= 0.09;
                if (shouldFlash)
                {
                    tickFlash_ = true;
                    accentFlash_ = isAccent;
                    lastFlashedBeatIndex_ = totalBeatIndex;
                }
                else if (lastFlashedBeatIndex_ == totalBeatIndex)
                {
                    tickFlash_ = false;
                    accentFlash_ = false;
                }
                else
                {
                    tickFlash_ = false;
                    accentFlash_ = false;
                }

                armAngleDeg_ = Math.Sin(elapsedBeats * Math.PI) * MaxArmAngleDeg;

                await InvokeAsync(StateHasChanged);
                await Task.Delay(16, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UpdateMidiVisualTempoAsync(int bpm)
    {
        bpm = Math.Clamp(bpm, MinBpm, MaxBpm);

        if (playState_ != PlayState.MIDI || !Enabled || midiVisualAudioAnchorTime_ is null)
            return;

        double audioTime;

        try
        {
            audioTime = await JS.InvokeAsync<double>("steelPan.getAudioTime");
        }
        catch
        {
            return;
        }

        var previousBpm = midiVisualTempoAnchorBpm_ > 0 ? midiVisualTempoAnchorBpm_ : bpm;
        var beatUnit = BeatUnit > 0 ? BeatUnit : 4;
        var previousSecondsPerBeat = (60.0 / previousBpm) * (4.0 / beatUnit);

        if (previousSecondsPerBeat > 0.0)
        {
            var elapsedAudioSeconds = Math.Max(0.0, audioTime - midiVisualAudioAnchorTime_.Value);
            midiVisualScoreAnchorSeconds_ += elapsedAudioSeconds;
        }

        midiVisualAudioAnchorTime_ = audioTime;
        midiVisualTempoAnchorBpm_ = bpm;
    }

    private async Task ClearFlashSoonAsync()
    {
        try
        {
            await Task.Delay(90);
            tickFlash_ = false;
            accentFlash_ = false;
            await InvokeAsync(StateHasChanged);
        }
        catch
        {
        }
    }

    private async Task StopLoopAsync()
    {
        if (loopCts_ is not null)
        {
            loopCts_.Cancel();
            loopCts_.Dispose();
            loopCts_ = null;
        }

        if (jsAvailable_)
        {
            try
            {
                await JS.InvokeVoidAsync("steelPan.stopMetronome");
            }
            catch
            {
            }
        }

        tickFlash_ = false;
        accentFlash_ = false;
        beatIndex_ = 0;
        displayBeat_ = 1;
        armAngleDeg_ = 0.0;
        lastFlashedBeatIndex_ = null;
    }

    private async Task StopMidiVisualSyncAsync(bool resetVisuals = true)
    {
        if (midiVisualCts_ is not null)
        {
            midiVisualCts_.Cancel();
            midiVisualCts_.Dispose();
            midiVisualCts_ = null;
        }

        if (jsAvailable_)
        {
            try
            {
                await JS.InvokeVoidAsync("steelPan.stopMetronome");
            }
            catch
            {
            }
        }

        if (resetVisuals)
        {
            midiStartAt_ = null;
            midiVisualStartOffsetSeconds_ = 0.0;
            midiVisualAudioAnchorTime_ = null;
            midiVisualScoreAnchorSeconds_ = 0.0;
            tickFlash_ = false;
            accentFlash_ = false;
            beatIndex_ = 0;
            displayBeat_ = 1;
            armAngleDeg_ = 0.0;
            lastFlashedBeatIndex_ = null;
        }
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static double GetWeightTopRatio(int bpm)
    {
        bpm = Math.Clamp(bpm, MinBpm, MaxBpm);

        if (bpm <= LargoBpm)
            return double.Lerp(WeightMinTopRatio, LargoTopRatio, InverseLerp(MinBpm, LargoBpm, bpm));

        if (bpm <= AndanteBpm)
            return double.Lerp(LargoTopRatio, AndanteTopRatio, InverseLerp(LargoBpm, AndanteBpm, bpm));

        if (bpm <= AllegroBpm)
            return double.Lerp(AndanteTopRatio, AllegroTopRatio, InverseLerp(AndanteBpm, AllegroBpm, bpm));

        return double.Lerp(AllegroTopRatio, WeightMaxTopRatio, InverseLerp(AllegroBpm, MaxBpm, bpm));
    }

    private static int GetBpmFromPointerY(double localPointerY, double trackHeightPx)
    {
        if (trackHeightPx <= 0.0)
            return MinBpm;

        var pointerRatio = localPointerY / trackHeightPx;
        var topRatio = Math.Clamp(pointerRatio - (WeightHeightRatio / 2.0), WeightMinTopRatio, WeightMaxTopRatio);

        double bpm;

        if (topRatio <= LargoTopRatio)
        {
            bpm = double.Lerp(MinBpm, LargoBpm, InverseLerp(WeightMinTopRatio, LargoTopRatio, topRatio));
        }
        else if (topRatio <= AndanteTopRatio)
        {
            bpm = double.Lerp(LargoBpm, AndanteBpm, InverseLerp(LargoTopRatio, AndanteTopRatio, topRatio));
        }
        else if (topRatio <= AllegroTopRatio)
        {
            bpm = double.Lerp(AndanteBpm, AllegroBpm, InverseLerp(AndanteTopRatio, AllegroTopRatio, topRatio));
        }
        else
        {
            bpm = double.Lerp(AllegroBpm, MaxBpm, InverseLerp(AllegroTopRatio, WeightMaxTopRatio, topRatio));
        }

        return (int)Math.Round(Math.Clamp(bpm, MinBpm, MaxBpm));
    }

    private static double InverseLerp(double start, double end, double value)
    {
        if (Math.Abs(end - start) < double.Epsilon)
            return 0.0;

        return Math.Clamp((value - start) / (end - start), 0.0, 1.0);
    }

    public async ValueTask DisposeAsync()
    {
        Playback.StateChanged -= OnPlaybackStateChangedAsync;
        Playback.PlaybackStarted -= OnPlaybackStartedAsync;
        Playback.PlaybackPaused -= OnPlaybackPausedAsync;
        Playback.PlaybackStopped -= OnPlaybackStoppedAsync;
        Playback.TempoChanged -= OnTempoChangedAsync;

        StopBpmRepeat();

        await StopLoopAsync();
        await StopMidiVisualSyncAsync(resetVisuals: false);
        selfRef_?.Dispose();
    }
}
