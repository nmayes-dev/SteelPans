using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Microsoft.AspNetCore.Components.Forms;
using SteelPans.WebApp.Model;

namespace SteelPans.WebApp.Services;

public sealed class MidiLoaderService
{
    public async Task<MidiFile> OpenMidiFileAsync(Stream midiStream)
    {
        await using MemoryStream buffer = new MemoryStream();

        await midiStream.CopyToAsync(buffer);
        buffer.Position = 0;

        return MidiFile.Read(buffer);
    }

    public List<MidiTrackInfo> GetTrackInfos(MidiFile file)
    {
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

    public async Task<MidiFile> MergeMidiTracksAsync(string name, IEnumerable<(string, Stream)> files)
    {
        var midiFileTasks = files.Select(async x =>
            {
                var (name, midiStream) = x;
                await using var buffer = new MemoryStream();
                await midiStream.CopyToAsync(buffer);
                buffer.Position = 0;

                return MidiFile.Read(buffer);
            });

        var midiFiles = await Task.WhenAll(midiFileTasks);
        var tempoMap = midiFiles[0].GetTempoMap();

        var trackChunks = midiFiles
            .Zip(files)
            .Select(data =>
            {
                var (midi, (name, stream)) = data;
                var notes = midi.GetNotes().ToList();
                var chunk = notes.ToTrackChunk();

                chunk.Events.Insert(0, new SequenceTrackNameEvent(Path.GetFileNameWithoutExtension(name))
                {
                    DeltaTime = 0
                });

                return chunk;
            })
            .ToList();

        return new MidiFile(trackChunks)
        {
            TimeDivision = midiFiles[0].TimeDivision
        };
    }


    public MidiPlaybackInfo GetPlaybackInfo(
        MidiFile midiFile,
        CancellationToken cancellationToken = default)
    {
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

    public List<MidiPanEvent> LoadSingleTrack(
        MidiFile midiFile,
        int trackIndex = 0,
        CancellationToken cancellationToken = default)
    {
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

        return events.Where(x => x.Duration > TimeSpan.Zero).ToList();
    }

    public List<(MidiTrackInfo Track, List<MidiPanEvent> Events)> LoadPlayableTracks(MidiFile midiFile)
    {
        var tempoMap = midiFile.GetTempoMap();
        var trackChunks = midiFile.GetTrackChunks().ToList();

        var result = new List<(MidiTrackInfo Track, List<MidiPanEvent> Events)>();

        for (var i = 0; i < trackChunks.Count; i++)
        {
            var track = trackChunks[i];
            var notes = track.GetNotes().OrderBy(n => n.Time).ToList();

            var events = new List<MidiPanEvent>(notes.Count);

            foreach (var midiNote in notes)
            {
                var startMetric = TimeConverter.ConvertTo<MetricTimeSpan>(midiNote.Time, tempoMap);
                var durationMetric = LengthConverter.ConvertTo<MetricTimeSpan>(
                    midiNote.Length,
                    midiNote.Time,
                    tempoMap);

                var duration = ToTimeSpan(durationMetric);
                if (duration <= TimeSpan.Zero)
                    continue;

                events.Add(new MidiPanEvent
                {
                    Note = Model.Note.FromMidi((int)midiNote.NoteNumber),
                    Start = ToTimeSpan(startMetric),
                    Duration = duration,
                });
            }

            if (events.Count == 0)
                continue;

            result.Add((
                new MidiTrackInfo
                {
                    Index = i,
                    Name = track.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text,
                    NoteCount = events.Count,
                },
                events));
        }

        return result;
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