using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Законченная встреча в своём каталоге: звук <c>audio.wav</c> без темпа.
/// </summary>
/// <remarks>
/// Законченной встреча считается по строке конца записи в <c>events.log</c>: <c>audio.wav</c> идущей встречи
/// растёт, и его начало сошло бы за всю встречу.
/// </remarks>
internal sealed class RecordedMeetingFolder : IRecordedMeetings
{
    public async Task<Result<RecordedMeeting>> Open(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return Result.Failure<RecordedMeeting>(StorageErrors.MeetingNotFound(directory));
        }

        string audio = Path.Combine(directory, LiveCapture.AudioFileName);

        if (!File.Exists(audio))
        {
            return Result.Failure<RecordedMeeting>(StorageErrors.AudioNotFound(audio));
        }

        if (!await Ended(Path.Combine(directory, EventsFile.FileName), cancellationToken))
        {
            return Result.Failure<RecordedMeeting>(StorageErrors.RecordingNotFinished(directory));
        }

        Result<IAudioSource> source = WavFileAudioSource.Open(audio, WavFileAudioSource.Unpaced, TimeProvider.System);

        if (source.IsFailure)
        {
            return Result.Failure<RecordedMeeting>(source.Error);
        }

        using var reader = new WaveFileReader(audio);

        return Result.Success(new RecordedMeeting(source.Value, SpeechAudio.Duration(reader.SampleCount)));
    }

    private static async Task<bool> Ended(string events, CancellationToken cancellationToken)
    {
        if (!File.Exists(events))
        {
            return false;
        }

        string[] lines = await File.ReadAllLinesAsync(events, cancellationToken);

        return lines.Any(line => line.EndsWith($"\t{MeetingFileFormat.EndOfRecording}", StringComparison.Ordinal));
    }
}
