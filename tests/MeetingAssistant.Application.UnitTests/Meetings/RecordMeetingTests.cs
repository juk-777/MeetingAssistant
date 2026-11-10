using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace MeetingAssistant.Application.UnitTests.Meetings;

public sealed class RecordMeetingTests : IDisposable
{
    private const string Text = "Реплика";

    // Предел ожидания: конвейер, который не останавливает подачу, должен ронять тест, а не вешать прогон.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly ScenarioHarness _harness = new();

    // Полем, а не using-переменной теста: её захватывает лямбда двойника, живущая дольше теста.
    private readonly ManualResetEventSlim _recognizerRelease = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Record_WhenBothChannelsSpeak_RecordsEachPhraseInItsChannelFromItsStart()
    {
        _harness.RunningMeeting();
        _harness.RecognizerReturns(Text);
        var source = new ScriptedSource(
        [
            ScriptedSource.Seconds(Channel.Me, 1),
            ScriptedSource.Seconds(Channel.Others, 2),
            ScriptedSource.Seconds(Channel.Me, 3),
            ScriptedSource.Seconds(Channel.Others, 1),
        ]);

        Result result = await _harness.Recorder().Record(source, Token).WaitAsync(Limit, Token);

        result.IsSuccess.ShouldBeTrue();
        RecordedSpans().ShouldBe(
            [
                (Channel.Me, Seconds(0), Seconds(1)),
                (Channel.Me, Seconds(1), Seconds(3)),
                (Channel.Others, Seconds(0), Seconds(2)),
                (Channel.Others, Seconds(2), Seconds(1)),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Record_WhenSourceEnds_RecognizesPhraseCutOffByEndAndReleasesDetectors()
    {
        _harness.RunningMeeting();
        _harness.RecognizerReturns(Text);
        var source = new ScriptedSource([ScriptedSource.Seconds(Channel.Others, 2)]);

        await _harness.Recorder().Record(source, Token).WaitAsync(Limit, Token);

        RecordedSpans().ShouldBe([(Channel.Others, Seconds(0), Seconds(2))]);
        _harness.Detectors.Created.ShouldAllBe(detector => detector.Disposed);
    }

    [Fact]
    public async Task Record_WhenRecognitionPending_StopsMeetingOnlyAfterLastPhraseWritten()
    {
        Meeting meeting = _harness.RunningMeeting();
        var recognizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognized = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.Recognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                recognizing.TrySetResult();
                return recognized.Task;
            });
        var source = new ScriptedSource([ScriptedSource.Seconds(Channel.Me, 1)]);

        Task<Result> recording = _harness.Recorder().Record(source, Token);
        await recognizing.Task.WaitAsync(Limit, Token);

        recording.IsCompleted.ShouldBeFalse();
        meeting.IsRunning.ShouldBeTrue();

        recognized.SetResult(Text);
        Result result = await recording.WaitAsync(Limit, Token);

        result.IsSuccess.ShouldBeTrue();
        RecordedSpans().ShouldBe([(Channel.Me, Seconds(0), Seconds(1))]);
        await _harness.WakeEvents.Received(1).AppendEnd(
            new MeetingStopped(1, _harness.Time.GetUtcNow()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Record_WhenCancelled_RecognizesCutPhrasesAndWritesEndWithLiveToken()
    {
        _harness.RunningMeeting();
        _harness.RecognizerReturns(Text);
        var source = new ScriptedSource(
            [ScriptedSource.Seconds(Channel.Me, 1), ScriptedSource.Seconds(Channel.Me, 2)],
            endless: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<Result> recording = _harness.Recorder().Record(source, cancellation.Token);
        await source.Exhausted.WaitAsync(Limit, Token);
        await cancellation.CancelAsync();
        Result result = await recording.WaitAsync(Limit, Token);

        result.IsSuccess.ShouldBeTrue();
        RecordedSpans().ShouldBe(
            [(Channel.Me, Seconds(0), Seconds(1)), (Channel.Me, Seconds(1), Seconds(2))]);
        await _harness.Recognizer.Received(2).Recognize(
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
        await _harness.WakeEvents.Received(1).AppendEnd(
            Arg.Any<MeetingStopped>(),
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
    }

    [Fact]
    public async Task Record_WhenTranscriptWriterFails_StopsFeedingAndRethrowsWithoutEnd()
    {
        Meeting meeting = _harness.RunningMeeting();
        _harness.RecognizerReturns(Text);
        _harness.Transcript.Append(Arg.Any<Utterance>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Нет места на диске"));
        var source = new ScriptedSource(
            [ScriptedSource.Seconds(Channel.Me, 1), ScriptedSource.Seconds(Channel.Me, 1)],
            endless: true);
        IMeetingRecorder recorder = _harness.Recorder();

        await Should.ThrowAsync<IOException>(() => recorder.Record(source, Token).WaitAsync(Limit, Token));

        meeting.IsRunning.ShouldBeTrue();
        await _harness.WakeEvents.DidNotReceive().AppendEnd(Arg.Any<MeetingStopped>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Record_WhenDetectorFlushFails_RethrowsAfterRecognizingCutPhrasesInsteadOfHanging()
    {
        _harness.RunningMeeting();
        _harness.RecognizerReturns(Text);
        _harness.Detectors.FailFlush = true;
        var source = new ScriptedSource(
            [ScriptedSource.Seconds(Channel.Me, 1), ScriptedSource.Seconds(Channel.Me, 1), ScriptedSource.Seconds(Channel.Others, 1)]);
        IMeetingRecorder recorder = _harness.Recorder();

        await Should.ThrowAsync<InvalidOperationException>(() => recorder.Record(source, Token).WaitAsync(Limit, Token));

        RecordedSpans().ShouldBe([(Channel.Me, Seconds(0), Seconds(1))]);
        _harness.Detectors.Created.ShouldAllBe(detector => detector.Disposed);
    }

    [Fact]
    public async Task Record_WhenRecognizerHoldsItsThread_FeedingGoesOn()
    {
        _harness.RunningMeeting();
        _harness.Recognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Как GigaAM: распознавание синхронно занимает поток читателя.
                _recognizerRelease.Wait(Limit);
                return Task.FromResult(Text);
            });
        var source = new ScriptedSource(
            [.. Enumerable.Range(0, 5).Select(_ => ScriptedSource.Seconds(Channel.Me, 1))]);

        Task<Result> recording = _harness.Recorder().Record(source, Token);
        bool fedWhileRecognizing = await Task.WhenAny(source.Exhausted, Task.Delay(Limit, Token)) == source.Exhausted;
        _recognizerRelease.Set();
        await recording.WaitAsync(Limit, Token);

        fedWhileRecognizing.ShouldBeTrue();
    }

    [Fact]
    public async Task Record_WhenNoMeeting_FailsWithNotRunningAndStopsFeeding()
    {
        _harness.RecognizerReturns(Text);
        var source = new ScriptedSource(
            [ScriptedSource.Seconds(Channel.Me, 1), ScriptedSource.Seconds(Channel.Me, 1)],
            endless: true);

        Result result = await _harness.Recorder().Record(source, Token).WaitAsync(Limit, Token);

        result.Error.ShouldBe(MeetingScenarioErrors.NotRunning);
        await _harness.WakeEvents.DidNotReceive().AppendEnd(Arg.Any<MeetingStopped>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        // Упавший тест мог оставить распознавание ждать: сначала отпустить, потом освобождать.
        _recognizerRelease.Set();
        _recognizerRelease.Dispose();
        _harness.Dispose();
    }

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    private List<(Channel Channel, TimeSpan Start, TimeSpan Duration)> RecordedSpans() =>
    [
        .. _harness.Transcript.ReceivedCalls()
            .Select(call => (Utterance)call.GetArguments()[0]!)
            .Select(utterance => (utterance.Channel, utterance.Span.Start, utterance.Span.Duration)),
    ];
}
