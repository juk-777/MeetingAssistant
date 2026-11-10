using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recognize;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using NSubstitute;
using Shouldly;

namespace MeetingAssistant.Application.UnitTests.Meetings;

public sealed class RecognizeSpeechTests : IDisposable
{
    private const string Statement = "Продолжаем обсуждение";

    private static readonly SpeechSpan Span = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
    private static readonly float[] Samples = new float[320];

    private readonly ScenarioHarness _harness = new();

    [Fact]
    public async Task Handle_WhenNoMeeting_FailsWithNotRunningWithoutRecognition()
    {
        Result result = await Recognize();

        result.Error.ShouldBe(MeetingScenarioErrors.NotRunning);
        await AssertNotRecognized();
    }

    [Fact]
    public async Task Handle_WhenMeetingStopped_FailsWithNotRunningWithoutRecognition()
    {
        _harness.RunningMeeting().Stop(ScenarioHarness.Now);

        Result result = await Recognize();

        result.Error.ShouldBe(MeetingScenarioErrors.NotRunning);
        await AssertNotRecognized();
    }

    [Fact]
    public async Task Handle_WhenTextRecognized_PassesSamplesAndAppendsTrimmedUtterance()
    {
        Meeting meeting = _harness.RunningMeeting();
        _harness.RecognizerReturns($"  {Statement} ");

        Result result = await Recognize();

        result.IsSuccess.ShouldBeTrue();
        await _harness.Recognizer.Received(1).Recognize(
            Arg.Is<ReadOnlyMemory<float>>(samples => samples.Length == Samples.Length),
            Arg.Any<CancellationToken>());
        await _harness.Transcript.Received(1).Append(
            new Utterance(1, Channel.Others, Span, Statement, _harness.Time.GetUtcNow()),
            Arg.Any<CancellationToken>());
        meeting.LastUtteranceNumber.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WhenRecognizedTextIsBlank_SucceedsWithoutUtterance(string text)
    {
        Meeting meeting = _harness.RunningMeeting();
        _harness.RecognizerReturns(text);

        Result result = await Recognize();

        result.IsSuccess.ShouldBeTrue();
        meeting.LastUtteranceNumber.ShouldBe(0);
        await _harness.Transcript.DidNotReceive().Append(Arg.Any<Utterance>(), Arg.Any<CancellationToken>());
        await _harness.WakeEvents.DidNotReceive().AppendWake(Arg.Any<SessionWakeRequested>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPhraseWakesSession_WritesUtteranceBeforeWakeEvent()
    {
        _harness.RunningMeeting();
        _harness.RecognizerReturns("Клод, где это решение");

        await Recognize();

        Received.InOrder(() =>
        {
            _harness.Transcript.Append(Arg.Is<Utterance>(u => u.Number == 1), Arg.Any<CancellationToken>());
            _harness.WakeEvents.AppendWake(
                Arg.Is<SessionWakeRequested>(wake => wake.UtteranceNumber == 1 && wake.Reasons.Contains(WakeReason.Address)),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Handle_WhenPhraseHasNoReason_WritesNoWakeEvent()
    {
        _harness.RunningMeeting();
        _harness.RecognizerReturns(Statement);

        await Recognize();

        await _harness.WakeEvents.DidNotReceive().AppendWake(Arg.Any<SessionWakeRequested>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenMeetingStopsDuringRecognition_FailsWithNotRunningWithoutUtterance()
    {
        Meeting meeting = _harness.RunningMeeting();
        _harness.Recognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                meeting.Stop(ScenarioHarness.Now);
                return Statement;
            });

        Result result = await Recognize();

        result.Error.ShouldBe(MeetingScenarioErrors.NotRunning);
        meeting.LastUtteranceNumber.ShouldBe(0);
        await _harness.Transcript.DidNotReceive().Append(Arg.Any<Utterance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenMeetingRestartedDuringRecognition_DoesNotRecordPhraseIntoNewMeeting()
    {
        Meeting first = _harness.RunningMeeting();
        Meeting? second = null;
        _harness.Recognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                first.Stop(ScenarioHarness.Now);
                second = Meeting.Start("Вторая", Glossary.Empty, ScenarioHarness.Now);
                _harness.CurrentMeeting.Meeting = second;
                return Statement;
            });

        Result result = await Recognize();

        result.Error.ShouldBe(MeetingScenarioErrors.NotRunning);
        second.ShouldNotBeNull().LastUtteranceNumber.ShouldBe(0);
        await _harness.Transcript.DidNotReceive().Append(Arg.Any<Utterance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRecognitionTakesTime_StampsUtteranceWithEndOfRecognition()
    {
        _harness.RunningMeeting();
        TimeSpan recognitionTime = TimeSpan.FromSeconds(2);
        _harness.Recognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _harness.Time.Advance(recognitionTime);
                return Statement;
            });

        await Recognize();

        await _harness.Transcript.Received(1).Append(
            Arg.Is<Utterance>(u => u.RecognizedAt == ScenarioHarness.Now + recognitionTime),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTranscriptWriteIsPending_DoesNotWriteWakeEventUntilItCompletes()
    {
        _harness.RunningMeeting();
        _harness.RecognizerReturns("Клод, где это решение");
        var transcriptWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Transcript.Append(Arg.Any<Utterance>(), Arg.Any<CancellationToken>()).Returns(transcriptWritten.Task);

        Task<Result> handling = Recognize();
        await _harness.Transcript.Received(1).Append(Arg.Any<Utterance>(), Arg.Any<CancellationToken>());
        await _harness.WakeEvents.DidNotReceive().AppendWake(Arg.Any<SessionWakeRequested>(), Arg.Any<CancellationToken>());

        transcriptWritten.SetResult();
        await handling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await _harness.WakeEvents.Received(1).AppendWake(Arg.Any<SessionWakeRequested>(), Arg.Any<CancellationToken>());
    }

    public void Dispose() => _harness.Dispose();

    private Task<Result> Recognize() =>
        _harness.Handler<RecognizeSpeech>()
            .Handle(new RecognizeSpeech(Channel.Others, Span, Samples), TestContext.Current.CancellationToken);

    private async Task AssertNotRecognized() =>
        await _harness.Recognizer.DidNotReceive().Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>());
}
