using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Application.Meetings.Start;

internal sealed class StartMeetingHandler(
    ICurrentMeeting currentMeeting,
    IMeetingStorage meetingStorage,
    TimeProvider timeProvider) : ICommandHandler<StartMeeting, Guid>
{
    public async Task<Result<Guid>> Handle(StartMeeting command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Result.Failure<Guid>(MeetingScenarioErrors.NameRequired);
        }

        if (currentMeeting.Meeting is { IsRunning: true })
        {
            return Result.Failure<Guid>(MeetingScenarioErrors.AlreadyRunning);
        }

        Result<Glossary> glossary = Glossary.Create(command.GlossaryLines);

        if (glossary.IsFailure)
        {
            return Result.Failure<Guid>(glossary.Error);
        }

        var meeting = Meeting.Start(command.Name, glossary.Value, timeProvider.GetUtcNow());

        await meetingStorage.Create(meeting, cancellationToken);
        await currentMeeting.Set(meeting, cancellationToken);

        return Result.Success(meeting.Id);
    }
}
