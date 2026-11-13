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
/// <br/>Кусок редкого канала набирается долго и распознаётся позже кусков другого, поэтому строки копятся и пишутся
/// в конце по времени записи: транскрипт читается по порядку разговора.
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

        Result<IRefinedTranscriptDraft> begun = await refinedTranscripts.Begin(command.Directory, cancellationToken);

        if (begun.IsFailure)
        {
            return begun;
        }

        RecordedMeeting meeting = opened.Value;
        await using IRefinedTranscriptDraft draft = begun.Value;
        var refinement = new Refinement(meeting.Duration, refineRecognizer, command.Progress, timeProvider);
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

        foreach (Utterance utterance in refinement.InRecordingOrder())
        {
            await draft.Append(utterance, cancellationToken);
        }

        await draft.Complete(cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Куски каналов и распознанные отрезки; отрезок с текстом — будущая строка второго транскрипта.
    /// </summary>
    private sealed class Refinement(
        TimeSpan duration,
        IRefineRecognizer refineRecognizer,
        IProgress<RefinePosition> progress,
        TimeProvider timeProvider)
    {
        private readonly Dictionary<Channel, SpeechGroup> _groups = [];
        private readonly List<(Channel Channel, SpeechSpan Span, string Text, DateTimeOffset RecognizedAt)> _lines = [];
        private readonly long _started = timeProvider.GetTimestamp();
        private TimeSpan _speech;
        private TimeSpan _position;

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

        /// <summary>
        /// Строки по началу на шкале записи, номер — с 1 в этом порядке.
        /// </summary>
        public IEnumerable<Utterance> InRecordingOrder() =>
            _lines
                .OrderBy(line => line.Span.Start)
                .ThenBy(line => line.Channel)
                .Select((line, index) => new Utterance(index + 1, line.Channel, line.Span, line.Text, line.RecognizedAt));

        private async Task Recognize(Channel channel, SpeechGroup group, CancellationToken cancellationToken)
        {
            IReadOnlyList<RecognizedSegment> segments = await refineRecognizer.Recognize(group.Samples(), cancellationToken);
            DateTimeOffset recognizedAt = timeProvider.GetUtcNow();

            foreach (RecognizedSegment segment in segments.Where(segment => !string.IsNullOrWhiteSpace(segment.Text)))
            {
                _lines.Add((channel, group.ToRecording(segment), segment.Text.Trim(), recognizedAt));
            }

            _speech += group.Speech;

            // Кусок редкого канала кончается раньше уже пройденного другим каналом: ход назад не идёт.
            _position = group.End > _position ? group.End : _position;
            progress.Report(new RefinePosition(_position, duration, _lines.Count, _speech, timeProvider.GetElapsedTime(_started)));
            group.Clear();
        }
    }
}
