using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace MeetingAssistant.Application.UnitTests.Meetings;

public sealed class RefineMeetingTests : IDisposable
{
    private const string Directory = "встреча";
    private const string Text = "Реплика";

    // Предел ожидания: сценарий, который ждёт не того, должен ронять тест, а не вешать прогон.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);

    private static readonly Error Refused = new("Refine.Refused", "Отказ");

    private readonly ScenarioHarness _harness = new();

    // Полем, а не using-переменной теста: её захватывает лямбда двойника.
    private readonly CancellationTokenSource _cancellation = new();

    private readonly IProgress<RefinePosition> _progress = Substitute.For<IProgress<RefinePosition>>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Handle_WhenBothChannelsSpeak_WritesEachPhraseWithItsChannelAndStartNumberedFromOne()
    {
        Recording(
            ScriptedSource.Seconds(Channel.Me, 1),
            ScriptedSource.Seconds(Channel.Others, 2),
            ScriptedSource.Seconds(Channel.Me, 3),
            ScriptedSource.Seconds(Channel.Others, 1));
        RecognizerReturns(Text);

        Result result = await Refine();

        result.IsSuccess.ShouldBeTrue();
        DraftTranscript draft = Draft();
        draft.Directory.ShouldBe(Directory);
        draft.Lines.Select(line => (line.Number, line.Channel, line.Span.Start, line.Span.Duration, line.Text)).ShouldBe(
        [
            (1, Channel.Me, Seconds(0), Seconds(1), Text),
            (2, Channel.Others, Seconds(0), Seconds(2), Text),
            (3, Channel.Me, Seconds(1), Seconds(3), Text),
            (4, Channel.Others, Seconds(2), Seconds(1), Text),
        ]);
        draft.Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_WhenSourceEnds_RecognizesPhraseCutOffByEndAndReleasesDetectors()
    {
        Recording(ScriptedSource.Seconds(Channel.Others, 2));
        RecognizerReturns(Text);

        await Refine();

        Draft().Lines.Select(line => (line.Channel, line.Span.Start)).ShouldBe([(Channel.Others, Seconds(0))]);
        _harness.Detectors.Created.ShouldAllBe(detector => detector.Disposed);
    }

    [Fact]
    public async Task Handle_WhenRecognizerReturnsBlank_WritesNoLineAndNumbersNextFromOne()
    {
        Recording(ScriptedSource.Seconds(Channel.Me, 1), ScriptedSource.Seconds(Channel.Me, 2));
        _harness.RefineRecognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(" ", $" {Text} ");

        await Refine();

        Draft().Lines.Select(line => (line.Number, line.Span.Start, line.Text)).ShouldBe([(1, Seconds(1), Text)]);
    }

    [Fact]
    public async Task Handle_WhenRecognitionPending_ReadsNoFurtherAudio()
    {
        var source = new CountingSource(
        [
            ScriptedSource.Seconds(Channel.Me, 1),
            ScriptedSource.Seconds(Channel.Me, 1),
            ScriptedSource.Seconds(Channel.Me, 1),
            ScriptedSource.Seconds(Channel.Me, 1),
        ]);
        Recording(source);
        var recognizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognized = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.RefineRecognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                recognizing.TrySetResult();
                return recognized.Task;
            });

        Task<Result> refining = Refine();
        await recognizing.Task.WaitAsync(Limit, Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Token);

        // Первая фраза — первый кусок, детектор отдаёт её на втором: дальше второго звук не читается.
        source.Taken.ShouldBe(2);
        recognized.SetResult(Text);
        (await refining.WaitAsync(Limit, Token)).IsSuccess.ShouldBeTrue();
        source.Taken.ShouldBe(4);
    }

    [Fact]
    public async Task Handle_WhenLastLineBeingWritten_CompletesOnlyAfterItIsWritten()
    {
        Recording(ScriptedSource.Seconds(Channel.Me, 1));
        RecognizerReturns(Text);
        var appending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.RefinedTranscripts.OnAppend = _ =>
        {
            appending.TrySetResult();
            return appended.Task;
        };

        Task<Result> refining = Refine();
        await appending.Task.WaitAsync(Limit, Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100), Token);

        Draft().Completed.ShouldBeFalse();
        appended.SetResult();
        await refining.WaitAsync(Limit, Token);
        Draft().Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_WhenMeetingRefused_FailsBeforeModelAndTranscript()
    {
        _harness.RecordedMeetings.Open(Directory, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RecordedMeeting>(Refused));

        Result result = await Refine();

        result.Error.ShouldBe(Refused);
        await _harness.RefineRecognizer.DidNotReceive().Prepare(Arg.Any<CancellationToken>());
        _harness.RefinedTranscripts.Draft.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_WhenModelNotPrepared_FailsBeforeTranscript()
    {
        Recording(ScriptedSource.Seconds(Channel.Me, 1));
        _harness.RefineRecognizer.Prepare(Arg.Any<CancellationToken>()).Returns(Result.Failure(Refused));

        Result result = await Refine();

        result.Error.ShouldBe(Refused);
        _harness.RefinedTranscripts.Draft.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_WhenRecognitionFails_AbandonsTranscriptAndReleasesDetectors()
    {
        Recording(ScriptedSource.Seconds(Channel.Me, 1), ScriptedSource.Seconds(Channel.Me, 1));
        _harness.RefineRecognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Распознаватель упал"));

        await Should.ThrowAsync<InvalidOperationException>(Refine());

        Draft().Abandoned.ShouldBeTrue();
        _harness.Detectors.Created.ShouldAllBe(detector => detector.Disposed);
    }

    [Fact]
    public async Task Handle_WhenCancelled_AbandonsTranscriptWithLinesWritten()
    {
        Recording(new CountingSource(
        [
            ScriptedSource.Seconds(Channel.Me, 1),
            ScriptedSource.Seconds(Channel.Me, 1),
            ScriptedSource.Seconds(Channel.Me, 1),
        ]));
        _harness.RefineRecognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(Text);
        _harness.RefinedTranscripts.OnAppend = async _ => await _cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            _harness.Handler<RefineMeeting>().Handle(new RefineMeeting(Directory, _progress), _cancellation.Token));

        Draft().Lines.ShouldNotBeEmpty();
        Draft().Abandoned.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_WhenPhraseRecognized_ReportsItsEndLinesSpeechAndRecognitionTime()
    {
        Recording(ScriptedSource.Seconds(Channel.Me, 2), ScriptedSource.Seconds(Channel.Me, 3));
        string[] texts = [Text, string.Empty];
        int call = 0;
        _harness.RefineRecognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _harness.Time.Advance(Seconds(3));
                return texts[call++];
            });

        await Refine();

        Received.InOrder(() =>
        {
            _progress.Report(new RefinePosition(Seconds(2), Duration, 1, Seconds(2), Seconds(3)));
            _progress.Report(new RefinePosition(Seconds(5), Duration, 1, Seconds(5), Seconds(6)));
        });
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _harness.Dispose();
    }

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    private void Recording(params AudioChunk[] chunks) => Recording(new ScriptedSource(chunks));

    private void Recording(IAudioSource source)
    {
        _harness.RecordedMeetings.Open(Directory, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RecordedMeeting(source, Duration)));
        _harness.RefineRecognizer.Prepare(Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private void RecognizerReturns(string text) =>
        _harness.RefineRecognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>()).Returns(text);

    private Task<Result> Refine() => _harness.Handler<RefineMeeting>().Handle(new RefineMeeting(Directory, _progress), Token);

    private DraftTranscript Draft() => _harness.RefinedTranscripts.Draft.ShouldNotBeNull();
}
