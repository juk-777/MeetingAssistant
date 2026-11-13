using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Shouldly;

namespace MeetingAssistant.Domain.UnitTests.Meetings;

public sealed class MeetingTests
{
    private const string Statement = "Продолжаем обсуждение";

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly SpeechSpan ShortSpan = new(TimeSpan.Zero, TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Start_WhenNameIsBlank_ThrowsNameRequired(string name)
    {
        DomainException exception = Should.Throw<DomainException>(() => Meeting.Start(name, Glossary.Empty, Start));

        exception.Code.ShouldBe(MeetingErrors.NameRequired);
    }

    [Fact]
    public void Start_WhenNameGiven_IsRunningWithoutUtterances()
    {
        Meeting meeting = Meeting.Start("  Планёрка  ", Glossary.Empty, Start);

        meeting.Name.ShouldBe("Планёрка");
        meeting.IsRunning.ShouldBeTrue();
        meeting.LastUtteranceNumber.ShouldBe(0);
        meeting.DequeueDomainEvents().ShouldBeEmpty();
    }

    [Fact]
    public void Record_WhenCalledRepeatedly_NumbersUtterancesFromOneWithoutGaps()
    {
        Meeting meeting = Running();

        int[] numbers = [.. Enumerable.Range(0, 3).Select(_ => Record(meeting, Statement).Number)];

        numbers.ShouldBe([1, 2, 3]);
        meeting.LastUtteranceNumber.ShouldBe(3);
    }

    [Fact]
    public void Record_WhenNoReason_RaisesOnlyUtteranceRecorded()
    {
        Meeting meeting = Running();

        Utterance utterance = Record(meeting, Statement);

        meeting.DequeueDomainEvents().ShouldBe([new UtteranceRecorded(utterance)]);
    }

    [Fact]
    public void Record_WhenReasonFound_RaisesWakeAfterUtteranceRecorded()
    {
        Meeting meeting = Running();
        DateTimeOffset recognizedAt = Start.AddSeconds(5);

        Utterance utterance = meeting.Record(Channel.Others, ShortSpan, "Агент, что скажешь", recognizedAt);

        IReadOnlyList<IDomainEvent> events = meeting.DequeueDomainEvents();
        events.Count.ShouldBe(2);
        events[0].ShouldBe(new UtteranceRecorded(utterance));
        SessionWakeRequested wake = events[1].ShouldBeOfType<SessionWakeRequested>();
        wake.UtteranceNumber.ShouldBe(utterance.Number);
        wake.Reasons.ShouldBe([WakeReason.Address]);
        wake.At.ShouldBe(recognizedAt);
    }

    [Fact]
    public void Record_WhenSpeechAccumulatesToLimit_WakesOnUtteranceThatReachesIt()
    {
        Meeting meeting = Running();
        SpeechSpan half = new(TimeSpan.Zero, WakePolicy.AccumulatedSpeechLimit / 2);

        meeting.Record(Channel.Me, half, Statement, Start);
        meeting.DequeueDomainEvents().OfType<SessionWakeRequested>().ShouldBeEmpty();

        meeting.Record(Channel.Me, half, Statement, Start);
        meeting.DequeueDomainEvents().OfType<SessionWakeRequested>().Single().Reasons
            .ShouldBe([WakeReason.AccumulatedSpeech]);
    }

    [Fact]
    public void Record_AfterAnyWake_CountsAccumulatedSpeechFromZero()
    {
        Meeting meeting = Running();
        SpeechSpan almostLimit = new(TimeSpan.Zero, WakePolicy.AccumulatedSpeechLimit - TimeSpan.FromSeconds(1));
        SpeechSpan twoSeconds = new(TimeSpan.Zero, TimeSpan.FromSeconds(2));

        meeting.Record(Channel.Me, almostLimit, Statement, Start);
        meeting.Record(Channel.Me, ShortSpan, "Агент, отметь", Start);
        meeting.DequeueDomainEvents();

        meeting.Record(Channel.Me, twoSeconds, Statement, Start);

        meeting.DequeueDomainEvents().OfType<SessionWakeRequested>().ShouldBeEmpty();
    }

    [Fact]
    public void Record_WhenTextIsBlank_ThrowsEmptyUtteranceAndKeepsNumbering()
    {
        Meeting meeting = Running();

        DomainException exception = Should.Throw<DomainException>(() => Record(meeting, "  "));

        exception.Code.ShouldBe(MeetingErrors.EmptyUtterance);
        Record(meeting, Statement).Number.ShouldBe(1);
    }

    [Fact]
    public void Record_WhenMeetingStopped_ThrowsStopped()
    {
        Meeting meeting = Running();
        meeting.Stop(Start);

        DomainException exception = Should.Throw<DomainException>(() => Record(meeting, Statement));

        exception.Code.ShouldBe(MeetingErrors.Stopped);
    }

    [Fact]
    public void Stop_WhenRunning_RaisesMeetingStoppedWithLastNumber()
    {
        Meeting meeting = Running();
        Record(meeting, Statement);
        Record(meeting, Statement);
        meeting.DequeueDomainEvents();
        DateTimeOffset stoppedAt = Start.AddMinutes(30);

        meeting.Stop(stoppedAt);

        meeting.IsRunning.ShouldBeFalse();
        meeting.StoppedAt.ShouldBe(stoppedAt);
        meeting.DequeueDomainEvents().ShouldBe([new MeetingStopped(2, stoppedAt)]);
    }

    [Fact]
    public void Stop_WhenAlreadyStopped_ThrowsAlreadyStopped()
    {
        Meeting meeting = Running();
        meeting.Stop(Start);

        DomainException exception = Should.Throw<DomainException>(() => meeting.Stop(Start));

        exception.Code.ShouldBe(MeetingErrors.AlreadyStopped);
    }

    [Fact]
    public void DequeueDomainEvents_WhenCalledTwice_ReturnsEventsOnce()
    {
        Meeting meeting = Running();
        Record(meeting, Statement);

        meeting.DequeueDomainEvents().ShouldNotBeEmpty();
        meeting.DequeueDomainEvents().ShouldBeEmpty();
    }

    private static Meeting Running() => Meeting.Start("Встреча", Glossary.Empty, Start);

    private static Utterance Record(Meeting meeting, string text) =>
        meeting.Record(Channel.Me, ShortSpan, text, Start);
}
