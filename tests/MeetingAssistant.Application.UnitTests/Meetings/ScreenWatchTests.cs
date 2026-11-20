using System.Threading.Channels;
using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.Domain.Screens;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace MeetingAssistant.Application.UnitTests.Meetings;

public sealed class ScreenWatchTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly ScenarioHarness _harness = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Record_WhenScreenGiven_WritesChangesAndStopsWatchingBeforeEndOfRecording()
    {
        _harness.RunningMeeting();
        var screen = new ScriptedScreen(second => second == 0 ? ScriptedScreen.Plain : ScriptedScreen.Changed);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<Result> recording = Record(screen, cancellation.Token);
        await StepTo(screen, ScreenChangePolicy.MinInterval);
        await cancellation.CancelAsync();
        (await recording.WaitAsync(Limit, Token)).IsSuccess.ShouldBeTrue();

        _harness.ScreenWriter.ReceivedCalls().Select(call => call.GetArguments()[1]).ShouldBe(
            [TimeSpan.Zero, ScreenChangePolicy.MinInterval]);
        await _harness.WakeEvents.Received(1).AppendEnd(Arg.Any<MeetingStopped>(), Arg.Any<CancellationToken>());

        int captured = screen.Captured;
        _harness.Time.Advance(ScreenChangePolicy.CaptureInterval * 5);
        screen.Captured.ShouldBe(captured);
    }

    [Fact]
    public async Task Record_WhenCaptureFailsOnce_LogsAndKeepsWatching()
    {
        _harness.RunningMeeting();
        var screen = new ScriptedScreen(second => second == 1
            ? throw new InvalidOperationException("Монитор отключён")
            : second == 0 ? ScriptedScreen.Plain : ScriptedScreen.Changed);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<Result> recording = Record(screen, cancellation.Token);
        await StepTo(screen, ScreenChangePolicy.MinInterval);
        await cancellation.CancelAsync();

        (await recording.WaitAsync(Limit, Token)).IsSuccess.ShouldBeTrue();
        _harness.ScreenWriter.ReceivedCalls().Count().ShouldBe(2);
        _harness.Log.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("экран"));
    }

    [Fact]
    public async Task Record_WhenCaptureFailsSeveralTimesInRow_LogsFirstFailureAndRecovery()
    {
        _harness.RunningMeeting();
        var screen = new ScriptedScreen(second => second is >= 1 and <= 3
            ? throw new InvalidOperationException("Экран заблокирован")
            : ScriptedScreen.Plain);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<Result> recording = Record(screen, cancellation.Token);
        await StepTo(screen, 5 * ScreenChangePolicy.CaptureInterval);
        await cancellation.CancelAsync();

        (await recording.WaitAsync(Limit, Token)).IsSuccess.ShouldBeTrue();
        _harness.Log.Entries.Count(entry => entry.Level == LogLevel.Warning).ShouldBe(1);
        _harness.Log.Entries.Count(entry => entry.Message.Contains("снова идёт")).ShouldBe(1);
    }

    [Fact]
    public async Task Record_WhenWriterFails_LogsAndKeepsWatching()
    {
        _harness.RunningMeeting();
        _harness.ScreenWriter.Write(Arg.Any<ScreenFrame>(), TimeSpan.Zero, Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("Нет места на диске"));
        var screen = new ScriptedScreen(second => second == 0 ? ScriptedScreen.Plain : ScriptedScreen.Changed);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<Result> recording = Record(screen, cancellation.Token);
        await StepTo(screen, ScreenChangePolicy.MinInterval);
        await cancellation.CancelAsync();

        (await recording.WaitAsync(Limit, Token)).IsSuccess.ShouldBeTrue();
        _harness.ScreenWriter.ReceivedCalls().Select(call => call.GetArguments()[1]).ShouldBe(
            [TimeSpan.Zero, ScreenChangePolicy.MinInterval]);
        _harness.Log.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning);
    }

    public void Dispose() => _harness.Dispose();

    private Task<Result> Record(ScriptedScreen screen, CancellationToken cancellationToken) =>
        _harness.Recorder().Record(new ScriptedSource([], endless: true), screen, cancellationToken);

    /// <summary>
    /// Шаг часов на секунду — только после снимка прошлой секунды: шаг, обогнавший снимок, склеил бы два тика.
    /// </summary>
    private async Task StepTo(ScriptedScreen screen, TimeSpan until)
    {
        for (int second = 0; ; second++)
        {
            await screen.WaitCaptured(second + 1).WaitAsync(Limit, Token);

            if (second * ScreenChangePolicy.CaptureInterval >= until)
            {
                return;
            }

            _harness.Time.Advance(ScreenChangePolicy.CaptureInterval);
        }
    }

    /// <summary>
    /// Экран, снимок которого задаёт номер снимка от начала наблюдения.
    /// </summary>
    private sealed class ScriptedScreen(Func<int, ScreenFrame> frame) : IScreenSource
    {
        public static readonly ScreenFrame Plain = Frame(changedCells: 0);

        public static readonly ScreenFrame Changed = Frame(changedCells: 20);

        private readonly Channel<int> _captures = System.Threading.Channels.Channel.CreateUnbounded<int>();
        private int _captured;

        public int Captured => Volatile.Read(ref _captured);

        public ScreenFrame Capture()
        {
            int number = Interlocked.Increment(ref _captured) - 1;

            try
            {
                return frame(number);
            }
            finally
            {
                _captures.Writer.TryWrite(number + 1);
            }
        }

        public async Task WaitCaptured(int count)
        {
            while (Captured < count)
            {
                await _captures.Reader.ReadAsync();
            }
        }

        private static ScreenFrame Frame(int changedCells)
        {
            byte[] rgb = new byte[ScreenGrid.Columns * ScreenGrid.Rows * 3];

            for (int point = 0; point < ScreenGrid.Columns * ScreenGrid.Rows; point++)
            {
                byte value = point < changedCells ? (byte)200 : (byte)100;
                rgb.AsSpan(point * 3, 3).Fill(value);
            }

            return new ScreenFrame(ScreenGrid.Columns, ScreenGrid.Rows, rgb);
        }
    }
}
