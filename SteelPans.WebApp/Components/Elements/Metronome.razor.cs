using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace SteelPans.WebApp.Components.Elements;

public partial class Metronome
{
    [Parameter]
    public bool MidiLoaded { get; set; }

    [Parameter]
    public double? MidiStartAt { get; set; }

    [Parameter]
    public int Bpm { get; set; } = 120;

    [Parameter]
    public EventCallback<int> BpmChanged { get; set; }

    [Parameter]
    public int BeatsPerBar { get; set; } = 4;

    [Parameter]
    public EventCallback<int> BeatsPerBarChanged { get; set; }

    [Parameter]
    public int BeatUnit { get; set; } = 4;

    [Parameter]
    public EventCallback<int> BeatUnitChanged { get; set; }

    [Parameter]
    public bool Enabled { get; set; } = true;

    [Parameter]
    public EventCallback<bool> EnabledChanged { get; set; }

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
    private bool CanEditBpm => Enabled && !IsPlaying;
    private bool CanEditTimeSignature => Enabled && !IsPlaying && !MidiLoaded;

    private double ArmAngleDeg => !Enabled || !EffectiveIsPlaying
        ? 0.0
        : armAngleDeg_;

    private double WeightTopPercent => GetWeightTopRatio(Bpm) * 100.0;
        
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            selfRef_ = DotNetObjectReference.Create(this);
            jsAvailable_ = true;
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!Enabled)
        {
            await StopAsync();
        }
    }

    private async Task BeginWeightDragAsync(PointerEventArgs e)
    {
        if (!CanEditBpm || selfRef_ is null)
            return;

        await JS.InvokeVoidAsync("steelPan.beginMetronomeWeightDrag", weightTrackRef_, selfRef_, e.ClientY);
    }

    [JSInvokable]
    public async Task OnMetronomeWeightDragged(double localPointerY, double trackHeightPx)
    {
        if (!CanEditBpm)
            return;

        var bpm = GetBpmFromPointerY(localPointerY, trackHeightPx);
        await SetBpmAsync(bpm);
    }

    public async Task StopAsync(bool resetVisuals = true)
    {
        if (playState_ == PlayState.NotPlaying)
            return;

        await StopLoopAsync();
        await StopMidiVisualSyncAsync(resetVisuals: resetVisuals);

        playState_ = PlayState.NotPlaying;
    }

    private async Task ToggleManualPlayAsync()
    {
        if (!Enabled || playState_ == PlayState.MIDI)
            return;

        if (playState_ == PlayState.Manual)
        {
            await StopAsync();
            return;
        }

        playState_ = PlayState.Manual;
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

    private async Task IncrementBeatsPerBarAsync()
    {
        await SetBeatsPerBarAsync(BeatsPerBar + 1);
    }

    private async Task DecrementBeatsPerBarAsync()
    {
        await SetBeatsPerBarAsync(BeatsPerBar - 1);
    }

    private async Task SetBpmAsync(int bpm)
    {
        bpm = Math.Clamp(bpm, MinBpm, MaxBpm);

        if (Bpm == bpm)
            return;

        Bpm = bpm;
        await BpmChanged.InvokeAsync(Bpm);

        if (Enabled && playState_ == PlayState.Manual)
            await RestartLoopAsync();
        else
            await InvokeAsync(StateHasChanged);
    }

    private async Task SetBeatsPerBarAsync(int beatsPerBar)
    {
        beatsPerBar = Math.Clamp(beatsPerBar, 1, 32);

        if (BeatsPerBar == beatsPerBar)
            return;

        BeatsPerBar = beatsPerBar;
        await BeatsPerBarChanged.InvokeAsync(BeatsPerBar);

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

        BeatUnit = beatUnit;
        await BeatUnitChanged.InvokeAsync(BeatUnit);

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
        await StopLoopAsync();

        if (!Enabled || playState_ != PlayState.Manual)
            return;

        loopCts_ = new CancellationTokenSource();
        var token = loopCts_.Token;

        beatIndex_ = 0;
        displayBeat_ = 1;
        armAngleDeg_ = 0.0;
        tickFlash_ = false;
        accentFlash_ = false;
        lastFlashedBeatIndex_ = null;

        _ = RunLoopAsync(token);

        await Task.CompletedTask;
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

    public async Task StartMidiVisualSyncAsync(double midiStartAt, double startOffsetSeconds = 0.0)
    {
        MidiStartAt = midiStartAt;
        midiVisualStartOffsetSeconds_ = Math.Max(0.0, startOffsetSeconds);

        if (!Enabled || !jsAvailable_)
            return;

        playState_ = PlayState.MIDI;
        midiVisualCts_ = new CancellationTokenSource();
        lastFlashedBeatIndex_ = null;

        _ = RunMidiVisualSyncAsync(midiVisualCts_.Token);

        await Task.CompletedTask;
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

                var elapsedSeconds = midiVisualStartOffsetSeconds_ + Math.Max(0.0, audioTime - MidiStartAt.Value);
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
        if (playState_ != PlayState.Manual)
            return;

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
        if (playState_ != PlayState.MIDI)
            return;

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
        await StopLoopAsync();
        await StopMidiVisualSyncAsync(resetVisuals: false);
        selfRef_?.Dispose();
    }
}
