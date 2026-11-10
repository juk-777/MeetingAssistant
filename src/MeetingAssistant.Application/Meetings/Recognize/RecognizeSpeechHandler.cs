using MeetingAssistant.Application.Abstractions.Events;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings.Recognize;

internal sealed class RecognizeSpeechHandler(
    ICurrentMeeting currentMeeting,
    ISpeechRecognizer speechRecognizer,
    IDomainEventDispatcher domainEventDispatcher,
    TimeProvider timeProvider) : ICommandHandler<RecognizeSpeech>
{
    public async Task<Result> Handle(RecognizeSpeech command, CancellationToken cancellationToken)
    {
        if (currentMeeting.Meeting is not { IsRunning: true } meeting)
        {
            return Result.Failure(MeetingScenarioErrors.NotRunning);
        }

        string text = await speechRecognizer.Recognize(command.Samples, cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return Result.Success();
        }

        // Распознавание длится до секунд: за это время встречу, которой принадлежит фраза, могли остановить,
        // а следующую — начать. Фраза пишется только в свою встречу.
        if (!meeting.IsRunning)
        {
            return Result.Failure(MeetingScenarioErrors.NotRunning);
        }

        meeting.Record(command.Channel, command.Span, text.Trim(), timeProvider.GetUtcNow());
        await domainEventDispatcher.Dispatch(meeting.DequeueDomainEvents(), cancellationToken);

        return Result.Success();
    }
}
