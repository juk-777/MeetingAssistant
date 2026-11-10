using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Start;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using NSubstitute;
using Shouldly;

namespace MeetingAssistant.Application.UnitTests.Meetings;

public sealed class StartMeetingTests : IDisposable
{
    private readonly ScenarioHarness _harness = new();

    [Fact]
    public async Task Handle_WhenNoMeeting_CreatesStorageAndBecomesCurrent()
    {
        Result<Guid> result = await Start(new StartMeeting("Планёрка", []));

        result.IsSuccess.ShouldBeTrue();
        Meeting meeting = _harness.CurrentMeeting.Meeting.ShouldNotBeNull();
        meeting.Id.ShouldBe(result.Value);
        meeting.IsRunning.ShouldBeTrue();
        meeting.StartedAt.ShouldBe(_harness.Time.GetUtcNow());
        await _harness.Storage.Received(1).Create(meeting, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenGlossaryGiven_MeetingUsesIt()
    {
        await Start(new StartMeeting("Планёрка", ["outbox"]));

        _harness.CurrentMeeting.Meeting.ShouldNotBeNull().Glossary.Matches("про outbox").ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Handle_WhenNameIsBlank_FailsWithNameRequired(string name)
    {
        Result<Guid> result = await Start(new StartMeeting(name, []));

        result.Error.ShouldBe(MeetingScenarioErrors.NameRequired);
        await AssertNothingCreated();
    }

    [Fact]
    public async Task Handle_WhenMeetingIsRunning_FailsWithAlreadyRunning()
    {
        Meeting running = _harness.RunningMeeting();

        Result<Guid> result = await Start(new StartMeeting("Вторая", []));

        result.Error.ShouldBe(MeetingScenarioErrors.AlreadyRunning);
        _harness.CurrentMeeting.Meeting.ShouldBeSameAs(running);
        await AssertNothingCreated();
    }

    [Fact]
    public async Task Handle_WhenPreviousMeetingStopped_StartsNewOne()
    {
        Meeting previous = _harness.RunningMeeting();
        previous.Stop(ScenarioHarness.Now);

        Result<Guid> result = await Start(new StartMeeting("Вторая", []));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBe(previous.Id);
    }

    [Fact]
    public async Task Handle_WhenGlossaryHasInvalidPattern_FailsWithInvalidPattern()
    {
        Result<Guid> result = await Start(new StartMeeting("Планёрка", ["(незакрытая"]));

        result.Error.Code.ShouldBe(Glossary.InvalidPatternCode);
        await AssertNothingCreated();
    }

    public void Dispose() => _harness.Dispose();

    private Task<Result<Guid>> Start(StartMeeting command) =>
        _harness.Handler<StartMeeting, Guid>().Handle(command, TestContext.Current.CancellationToken);

    private async Task AssertNothingCreated()
    {
        await _harness.Storage.DidNotReceive().Create(Arg.Any<Meeting>(), Arg.Any<CancellationToken>());
    }
}
