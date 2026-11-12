using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Application.Meetings.Refine;

/// <remarks>
/// Фраза распознаётся сразу, как детектор её отдал, и следующий кусок звука читается только после этого: звук
/// с диска идёт быстрее распознавания, и очередь фраз копила бы в памяти звук всей встречи.
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

                await refinement.Recognize(chunk.Channel, detector.Accept(chunk.Samples), cancellationToken);
            }

            foreach ((Channel channel, IVoiceActivityDetector detector) in detectors)
            {
                await refinement.Recognize(channel, detector.Flush(), cancellationToken);
            }
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
    /// Строки второго транскрипта: свой сквозной номер с 1.
    /// </summary>
    private sealed class Refinement(
        IRefinedTranscriptDraft draft,
        TimeSpan duration,
        IRefineRecognizer refineRecognizer,
        IProgress<RefinePosition> progress,
        TimeProvider timeProvider)
    {
        private int _lastNumber;

        public async Task Recognize(Channel channel, IReadOnlyList<DetectedPhrase> phrases, CancellationToken cancellationToken)
        {
            foreach (DetectedPhrase phrase in phrases)
            {
                var span = new SpeechSpan(phrase.Start, SpeechAudio.Duration(phrase.Samples.Length));
                string text = await refineRecognizer.Recognize(phrase.Samples, cancellationToken);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    _lastNumber++;
                    await draft.Append(
                        new Utterance(_lastNumber, channel, span, text.Trim(), timeProvider.GetUtcNow()),
                        cancellationToken);
                }

                progress.Report(new RefinePosition(span.Start + span.Duration, duration, _lastNumber));
            }
        }
    }
}
