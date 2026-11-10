using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Stop;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using NSubstitute;
using Shouldly;

namespace MeetingAssistant.Application.UnitTests.Meetings;

public sealed class StopMeetingTests : IDisposable
{
    private readonly ScenarioHarness _harness = new();

    [Fact]
    public async Task Handle_WhenRunning_StopsAndWritesEndWithLastNumber()
    {
        Meeting meeting = _harness.RunningMeeting();
        meeting.Record(Channel.Me, new SpeechSpan(TimeSpan.Zero, TimeSpan.FromSeconds(1)), "Начинаем", ScenarioHarness.Now);
        meeting.DequeueDomainEvents();
        _harness.Time.Advance(TimeSpan.FromMinutes(5));

        Result result = await Stop();

        result.IsSuccess.ShouldBeTrue();
        meeting.IsRunning.ShouldBeFalse();
        await _harness.WakeEvents.Received(1).AppendEnd(
            new MeetingStopped(1, _harness.Time.GetUtcNow()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNoMeeting_FailsWithNotRunning()
    {
        Result result = await Stop();

        result.Error.ShouldBe(MeetingScenarioErrors.NotRunning);
    }

    [Fact]
    public async Task Handle_WhenAlreadyStopped_FailsWithNotRunningWithoutSecondEnd()
    {
        _harness.RunningMeeting().Stop(ScenarioHarness.Now);

        Result result = await Stop();

        result.Error.ShouldBe(MeetingScenarioErrors.NotRunning);
        await _harness.WakeEvents.DidNotReceive().AppendEnd(Arg.Any<MeetingStopped>(), Arg.Any<CancellationToken>());
    }

    public void Dispose() => _harness.Dispose();

    private Task<Result> Stop() =>
        _harness.Handler<StopMeeting>().Handle(new StopMeeting(), TestContext.Current.CancellationToken);
}
