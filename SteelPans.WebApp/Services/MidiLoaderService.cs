using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using SteelPans.WebApp.Model;

namespace SteelPans.WebApp.Services;

public sealed class MidiLoaderService
{
    public async Task<List<MidiTrackInfo>> GetTrackInfosAsync(Stream midiStream)
    {
        await using var buffer = new MemoryStream();
        await midiStream.CopyToAsync(buffer);
        buffer.Position = 0;

        var file = MidiFile.Read(buffer);

        return file.GetTrackChunks()
            .Select((track, i) => new MidiTrackInfo
            {
                Index = i,
                Name = track.Events
                    .OfType<SequenceTrackNameEvent>()
                    .FirstOrDefault()?.Text,
                NoteCount = track.GetNotes().Count()
            })
            .ToList();
    }

    public async Task<MidiPlaybackInfo> GetPlaybackInfoAsync(
    Stream midiStream,
    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(midiStream);

        if (!midiStream.CanRead)
            throw new ArgumentException("The MIDI stream must be readable.", nameof(midiStream));

        await using var buffer = new MemoryStream();
        await midiStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var midiFile = MidiFile.Read(buffer);
        var tempoMap = midiFile.GetTempoMap();

        var tempoChanges = new List<MidiTempoChange>();
        var timeSignatureChanges = new List<MidiTimeSignatureChange>();

        foreach (var timedEvent in midiFile.GetTimedEvents().OrderBy(e => e.Time))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var time = ToTimeSpan(TimeConverter.ConvertTo<MetricTimeSpan>(timedEvent.Time, tempoMap));

            switch (timedEvent.Event)
            {
                case SetTempoEvent tempoEvent:
                    tempoChanges.Add(new MidiTempoChange
                    {
                        Time = time,
                        Bpm = (int)Math.Round(60_000_000d / tempoEvent.MicrosecondsPerQuarterNote),
                    });
                    break;

                case TimeSignatureEvent timeSignatureEvent:
                    timeSignatureChanges.Add(new MidiTimeSignatureChange
                    {
                        Time = time,
                        Numerator = timeSignatureEvent.Numerator,
                        Denominator = timeSignatureEvent.Denominator,
                    });
                    break;
            }
        }

        var initialTempo = tempoChanges.FirstOrDefault(t => t.Time == TimeSpan.Zero)
            ?? tempoChanges.FirstOrDefault()
            ?? new MidiTempoChange
            {
                Time = TimeSpan.Zero,
                Bpm = 120,
            };

        var initialTimeSignature = timeSignatureChanges.FirstOrDefault(t => t.Time == TimeSpan.Zero)
            ?? timeSignatureChanges.FirstOrDefault()
            ?? new MidiTimeSignatureChange
            {
                Time = TimeSpan.Zero,
                Numerator = 4,
                Denominator = 4,
            };

        return new MidiPlaybackInfo
        {
            InitialBpm = initialTempo.Bpm,
            InitialBeatsPerBar = initialTimeSignature.Numerator,
            InitialBeatUnit = initialTimeSignature.Denominator,
            TempoChanges = tempoChanges,
            TimeSignatureChanges = timeSignatureChanges,
        };
    }

    public async Task<List<MidiPanEvent>> LoadSingleTrackAsync(
        Stream midiStream,
        int trackIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(midiStream);

        if (!midiStream.CanRead)
            throw new ArgumentException("The MIDI stream must be readable.", nameof(midiStream));

        using var buffer = new MemoryStream();
        await midiStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var midiFile = MidiFile.Read(buffer);
        var tempoMap = midiFile.GetTempoMap();

        var trackChunks = midiFile.GetTrackChunks().ToList();

        if (trackChunks.Count == 0)
            return [];

        if (trackIndex < 0 || trackIndex >= trackChunks.Count)
            throw new ArgumentOutOfRangeException(nameof(trackIndex));

        var notes = trackChunks[trackIndex]
            .GetNotes()
            .OrderBy(n => n.Time)
            .ToList();

        var events = new List<MidiPanEvent>(notes.Count);

        foreach (var midiNote in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startMetric = TimeConverter.ConvertTo<MetricTimeSpan>(
                midiNote.Time,
                tempoMap);

            var durationMetric = LengthConverter.ConvertTo<MetricTimeSpan>(
                midiNote.Length,
                midiNote.Time,
                tempoMap);

            events.Add(new MidiPanEvent
            {
                Note = Model.Note.FromMidi((int)midiNote.NoteNumber),
                Start = ToTimeSpan(startMetric),
                Duration = ToTimeSpan(durationMetric),
            });
        }

        return events;
    }


    public async Task<List<MidiPanEvent>> LoadMergedMidiAsync(
        Stream midiStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(midiStream);

        if (!midiStream.CanRead)
            throw new ArgumentException("The MIDI stream must be readable.", nameof(midiStream));

        using var buffer = new MemoryStream();
        await midiStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var midiFile = MidiFile.Read(buffer);
        var tempoMap = midiFile.GetTempoMap();

        var trackChunks = midiFile.GetTrackChunks().ToList();

        if (trackChunks.Count == 0)
            return [];

        var noteCount = trackChunks.Select(x => x.GetNotes().Count).Sum();

        var events = new List<MidiPanEvent>(noteCount);

        foreach (var midiNote in trackChunks.SelectMany(x => x.GetNotes()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startMetric = TimeConverter.ConvertTo<MetricTimeSpan>(
                midiNote.Time,
                tempoMap);

            var durationMetric = LengthConverter.ConvertTo<MetricTimeSpan>(
                midiNote.Length,
                midiNote.Time,
                tempoMap);

            events.Add(new MidiPanEvent
            {
                Note = Model.Note.FromMidi((int)midiNote.NoteNumber),
                Start = ToTimeSpan(startMetric),
                Duration = ToTimeSpan(durationMetric),
            });
        }

        return events;
    }
    private static TimeSpan ToTimeSpan(MetricTimeSpan m)
        => TimeSpan.FromHours(m.Hours)
         + TimeSpan.FromMinutes(m.Minutes)
         + TimeSpan.FromSeconds(m.Seconds)
         + TimeSpan.FromMilliseconds(m.Milliseconds);
}

public static class PanMidiMapper
{
    public static List<MidiPanEvent> FilterToPan(SteelPan pan, IEnumerable<MidiPanEvent> events)
    {
        return events
            .Where(e => pan.Notes.Any(n => n.Note.IsEnharmonicEquivalentTo(e.Note)))
            .ToList();
    }
}