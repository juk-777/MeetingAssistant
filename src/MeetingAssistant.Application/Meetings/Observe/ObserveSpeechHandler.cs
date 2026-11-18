using MeetingAssistant.Application.Abstractions.Events;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings.Observe;

internal sealed class ObserveSpeechHandler(
    ICurrentMeeting currentMeeting,
    IDomainEventDispatcher domainEventDispatcher,
    TimeProvider timeProvider) : ICommandHandler<ObserveSpeech>
{
    public async Task<Result> Handle(ObserveSpeech command, CancellationToken cancellationToken)
    {
        if (currentMeeting.Meeting is not { IsRunning: true } meeting)
        {
            return Result.Failure(MeetingScenarioErrors.NotRunning);
        }

        meeting.Observe(command.Me, command.Others, timeProvider.GetUtcNow());
        await domainEventDispatcher.Dispatch(meeting.DequeueDomainEvents(), cancellationToken);

        return Result.Success();
    }
}
