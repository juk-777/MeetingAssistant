using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Application.Meetings.Refine;

/// <remarks>
/// Фразы канала копятся в кусок до окна распознавателя (<see cref="SpeechGroup"/>), кусок распознаётся, как
/// только следующая фраза в него не помещается, и следующий кусок звука читается только после этого: звук с диска
/// идёт быстрее распознавания, и очередь копила бы в памяти звук всей встречи.
/// </remarks>
internal sealed class RefineMeetingHandler(
    IRecordedMeetings recordedMeetings,
    IRefineRecognizer refineRecognizer,
    IRefinedTranscripts refinedTranscripts,
    IVoiceActivityDetectorFactory detectorFactory,
    TimeProvider timeProvider) : ICommandHandler<RefineMeeting>
{
    public async Task<Result> Handle(RefineMeeting command, CancellationToken cancellationToken)
    {
        Result<RecordedMeeting> opened = await recordedMeetings.Open(command.Directory, cancellationToken);

        if (opened.IsFailure)
        {
            return opened;
        }

        Result prepared = await refineRecognizer.Prepare(cancellationToken);

        if (prepared.IsFailure)
        {
            return prepared;
        }

        RecordedMeeting meeting = opened.Value;
        await using IRefinedTranscriptDraft draft = await refinedTranscripts.Begin(command.Directory, cancellationToken);
        var refinement = new Refinement(draft, meeting.Duration, refineRecognizer, command.Progress, timeProvider);
        var detectors = new Dictionary<Channel, IVoiceActivityDetector>();

        try
        {
            await foreach (AudioChunk chunk in meeting.Audio.Read(cancellationToken))
            {
                if (!detectors.TryGetValue(chunk.Channel, out IVoiceActivityDetector? detector))
                {
                    detector = detectorFactory.Create();
                    detectors.Add(chunk.Channel, detector);
                }

                await refinement.Add(chunk.Channel, detector.Accept(chunk.Samples), cancellationToken);
            }

            foreach ((Channel channel, IVoiceActivityDetector detector) in detectors)
            {
                await refinement.Add(channel, detector.Flush(), cancellationToken);
            }

            await refinement.RecognizeRest(cancellationToken);
        }
        finally
        {
            foreach (IVoiceActivityDetector detector in detectors.Values)
            {
                detector.Dispose();
            }
        }

        await draft.Complete(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Куски каналов и строки второго транскрипта: свой сквозной номер с 1, строка — отрезок распознавателя.
    /// </summary>
    private sealed class Refinement(
        IRefinedTranscriptDraft draft,
        TimeSpan duration,
        IRefineRecognizer refineRecognizer,
        IProgress<RefinePosition> progress,
        TimeProvider timeProvider)
    {
        private readonly Dictionary<Channel, SpeechGroup> _groups = [];
        private readonly long _started = timeProvider.GetTimestamp();
        private int _lastNumber;
        private TimeSpan _speech;

        public async Task Add(Channel channel, IReadOnlyList<DetectedPhrase> phrases, CancellationToken cancellationToken)
        {
            foreach (DetectedPhrase phrase in phrases)
            {
                if (!_groups.TryGetValue(channel, out SpeechGroup? group))
                {
                    group = new SpeechGroup();
                    _groups.Add(channel, group);
                }

                if (!group.Fits(phrase, refineRecognizer.Window))
                {
                    await Recognize(channel, group, cancellationToken);
                }

                group.Add(phrase);
            }
        }

        public async Task RecognizeRest(CancellationToken cancellationToken)
        {
            foreach ((Channel channel, SpeechGroup group) in _groups)
            {
                if (!group.IsEmpty)
                {
                    await Recognize(channel, group, cancellationToken);
                }
            }
        }

        private async Task Recognize(Channel channel, SpeechGroup group, CancellationToken cancellationToken)
        {
            IReadOnlyList<RecognizedSegment> segments = await refineRecognizer.Recognize(group.Samples(), cancellationToken);

            foreach (RecognizedSegment segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                _lastNumber++;
                await draft.Append(
                    new Utterance(_lastNumber, channel, group.ToRecording(segment), segment.Text.Trim(), timeProvider.GetUtcNow()),
                    cancellationToken);
            }

            _speech += group.Speech;
            progress.Report(new RefinePosition(group.End, duration, _lastNumber, _speech, timeProvider.GetElapsedTime(_started)));
            group.Clear();
        }
    }
}
