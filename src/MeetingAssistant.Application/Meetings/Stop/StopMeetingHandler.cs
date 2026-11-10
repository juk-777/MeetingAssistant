using MeetingAssistant.Application.Abstractions.Events;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings.Stop;

internal sealed class StopMeetingHandler(
    ICurrentMeeting currentMeeting,
    IDomainEventDispatcher domainEventDispatcher,
    TimeProvider timeProvider) : ICommandHandler<StopMeeting>
{
    public async Task<Result> Handle(StopMeeting command, CancellationToken cancellationToken)
    {
        if (currentMeeting.Meeting is not { IsRunning: true } meeting)
        {
            return Result.Failure(MeetingScenarioErrors.NotRunning);
        }

        meeting.Stop(timeProvider.GetUtcNow());
        await domainEventDispatcher.Dispatch(meeting.DequeueDomainEvents(), cancellationToken);

        return Result.Success();
    }
}
