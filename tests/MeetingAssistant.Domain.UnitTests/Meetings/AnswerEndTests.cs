using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Shouldly;

namespace MeetingAssistant.Domain.UnitTests.Meetings;

public sealed class AnswerEndTests
{
    private const string Answer = "Я бы разделил сервис на два";
    private const string Reply = "Понятно, а как с транзакциями";

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(12.5, true)]
    [InlineData(12.4, false)]
    public void Observe_WhenBothSilentAfterAnswer_EndsAnswerOnlyAfterSilenceLimit(double position, bool ended)
    {
        Meeting meeting = WithAnswer(0, 10, out Utterance last);

        meeting.Observe(Silent(position), Silent(position), Start);

        Ends(meeting).ShouldBe(ended ? [new AnswerEnded(last.Number, Start)] : []);
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.9, false)]
    public void Observe_WhenOwnSpeechAfterAnswer_EndsAnswerBeforeSilenceLimit(double ownSpeech, bool ended)
    {
        Meeting meeting = WithAnswer(0, 10, out _);

        meeting.Observe(Speaking(10.5 + ownSpeech, since: 10.5), Silent(10.5 + ownSpeech), Start);

        Ends(meeting).Count.ShouldBe(ended ? 1 : 0);
    }

    [Fact]
    public void Observe_WhenOwnSpeechWhileOthersSpeak_WaitsForOthersToStop()
    {
        Meeting meeting = WithAnswer(0, 10, out _);

        // Детектор канала собеседников держит речь ещё 0.5 с после её конца.
        meeting.Observe(Speaking(11.3, since: 10.2), Speaking(11.3, since: 9), Start);
        Ends(meeting).ShouldBeEmpty();

        meeting.Observe(Speaking(11.5, since: 10.2), Silent(11.5), Start);
        Ends(meeting).Count.ShouldBe(1);
    }

    [Fact]
    public void Observe_WhenOwnSpeechStartedBeforeAnswerEnd_EndsAnswerOnlyAfterItStops()
    {
        Meeting meeting = WithAnswer(0, 10, out Utterance last);

        meeting.Observe(Speaking(13, since: 9.5), Silent(13), Start);
        Ends(meeting).ShouldBeEmpty();

        meeting.Observe(Silent(14), Silent(14), Start);
        Ends(meeting).ShouldBe([new AnswerEnded(last.Number, Start)]);
    }

    [Fact]
    public void Observe_WhenAnswerShorterThanMinimum_NeitherSilenceNorOwnSpeechEndsIt()
    {
        Meeting meeting = WithAnswer(0, 9.9, out _);

        meeting.Observe(Silent(20), Silent(20), Start);
        meeting.Observe(Speaking(22, since: 20.5), Silent(22), Start);

        Ends(meeting).ShouldBeEmpty();
    }

    [Fact]
    public void Observe_WhenOwnSpeechAfterShortReply_StartsAnswerAnew()
    {
        Meeting meeting = WithAnswer(0, 5, out _);

        meeting.Observe(Speaking(6.5, since: 5.5), Silent(6.5), Start);
        Phrase(meeting, 8, 14);
        meeting.Observe(Silent(17), Silent(17), Start);

        // Без сброса ответ шёл бы с 0 по 14 с и кончился бы.
        Ends(meeting).ShouldBeEmpty();
    }

    [Fact]
    public void Observe_WhenOwnBackchannelInsideAnswer_KeepsAnswerWhole()
    {
        Meeting meeting = WithAnswer(0, 6, out _);

        meeting.Observe(Speaking(7.4, since: 6.5), Silent(7.4), Start);
        meeting.Observe(Silent(7.8), Silent(7.8), Start);
        Utterance last = Phrase(meeting, 8, 12);
        meeting.Observe(Silent(14.5), Silent(14.5), Start);

        Ends(meeting).ShouldBe([new AnswerEnded(last.Number, Start)]);
    }

    [Fact]
    public void Observe_WhenAnswerSpanLongerThanItsPhrases_CountsSpan()
    {
        Meeting meeting = WithAnswer(0, 4, out _);
        Phrase(meeting, 7, 11);

        meeting.Observe(Silent(13.5), Silent(13.5), Start);

        Ends(meeting).Count.ShouldBe(1);
    }

    [Fact]
    public void Observe_WhenOneChannelLags_WaitsForSilenceInBoth()
    {
        Meeting meeting = WithAnswer(0, 10, out _);

        meeting.Observe(Silent(12.4), Silent(20), Start);

        Ends(meeting).ShouldBeEmpty();
    }

    [Fact]
    public void Observe_AfterAnswerEnded_DoesNotEndItAgain()
    {
        Meeting meeting = WithAnswer(0, 10, out _);
        meeting.Observe(Silent(12.5), Silent(12.5), Start);
        meeting.DequeueDomainEvents();

        meeting.Observe(Silent(15), Silent(15), Start);
        meeting.Observe(Speaking(17, since: 15.5), Silent(17), Start);

        Ends(meeting).ShouldBeEmpty();
    }

    [Fact]
    public void Observe_WhenOwnPhraseRecordedAfterAnswer_PointsAtLastOthersPhrase()
    {
        Meeting meeting = WithAnswer(0, 10, out Utterance last);
        meeting.Record(Channel.Me, new SpeechSpan(TimeSpan.FromSeconds(10.2), TimeSpan.FromSeconds(3)), Reply, Start);

        meeting.Observe(Speaking(14, since: 10.2), Silent(14), Start);

        Ends(meeting).ShouldBe([new AnswerEnded(last.Number, Start)]);
    }

    [Fact]
    public void Observe_WhenAnswerEnds_DoesNotResetAccumulatedSpeech()
    {
        Meeting meeting = WithAnswer(0, 40, out _);
        meeting.Observe(Silent(43), Silent(43), Start);

        Phrase(meeting, 50, 70);

        meeting.DequeueDomainEvents().OfType<SessionWakeRequested>().Single().Reasons
            .ShouldBe([WakeReason.AccumulatedSpeech]);
    }

    [Fact]
    public void Observe_WhenMeetingStopped_ThrowsStopped()
    {
        Meeting meeting = WithAnswer(0, 10, out _);
        meeting.Stop(Start);

        DomainException exception = Should.Throw<DomainException>(() => meeting.Observe(Silent(13), Silent(13), Start));

        exception.Code.ShouldBe(MeetingErrors.Stopped);
    }

    [Theory]
    [InlineData(-1.0, null)]
    [InlineData(5.0, -1.0)]
    [InlineData(5.0, 6.0)]
    public void SpeechActivity_WhenSpeechOutsideAudio_ThrowsInvalidSpeechActivity(double position, double? since)
    {
        DomainException exception = Should.Throw<DomainException>(() =>
            new SpeechActivity(Seconds(position), since is { } value ? Seconds(value) : null));

        exception.Code.ShouldBe(MeetingErrors.InvalidSpeechActivity);
    }

    private static Meeting WithAnswer(double start, double end, out Utterance last)
    {
        Meeting meeting = Meeting.Start("Собеседование", Glossary.Empty, Start);
        last = Phrase(meeting, start, end);
        return meeting;
    }

    private static Utterance Phrase(Meeting meeting, double start, double end) =>
        meeting.Record(Channel.Others, new SpeechSpan(Seconds(start), Seconds(end - start)), Answer, Start);

    private static List<AnswerEnded> Ends(Meeting meeting) => [.. meeting.DequeueDomainEvents().OfType<AnswerEnded>()];

    private static SpeechActivity Silent(double position) => new(Seconds(position), null);

    private static SpeechActivity Speaking(double position, double since) => new(Seconds(position), Seconds(since));

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
}
