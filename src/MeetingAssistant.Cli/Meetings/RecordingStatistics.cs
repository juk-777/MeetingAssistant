using System.Diagnostics;
using System.Globalization;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Cli.Meetings;

/// <summary>
/// Итог записи встречи: из него берутся цифры замеров (<c>docs/measurements.md</c>).
/// </summary>
internal sealed class RecordingStatistics
{
    private const double BytesPerMegabyte = 1024 * 1024;

    private readonly List<TimeSpan> _recognitions = [];
    private int _wakes;

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<RecordingStatistics>();
        services.Decorate<ISpeechRecognizer, TimedSpeechRecognizer>();
        services.AddTransient<IDomainEventSubscriber<SessionWakeRequested>, WakeCounter>();
    }

    public void AddRecognition(TimeSpan elapsed)
    {
        lock (_recognitions)
        {
            _recognitions.Add(elapsed);
        }
    }

    public void AddWake() => Interlocked.Increment(ref _wakes);

    public string Summary(int utterances, TimeSpan elapsed)
    {
        List<TimeSpan> sorted;

        lock (_recognitions)
        {
            sorted = [.. _recognitions.Order()];
        }

        string recognition = sorted.Count == 0
            ? "фраз нет"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"фраз {sorted.Count}, на фразу p50 {Seconds(sorted[sorted.Count / 2])}, " +
                $"p90 {Seconds(sorted[(int)(sorted.Count * 0.9)])}, max {Seconds(sorted[^1])} с");

        using Process process = Process.GetCurrentProcess();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Строк транскрипта {utterances}, событий {_wakes}; распознавание: {recognition}; прогон {elapsed.TotalSeconds:F1} с; " +
            $"CPU процесса {process.TotalProcessorTime.TotalSeconds:F1} с, пик памяти {process.PeakWorkingSet64 / BytesPerMegabyte:F0} МБ");
    }

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Время распознавания каждой фразы.
    /// </summary>
    private sealed class TimedSpeechRecognizer(ISpeechRecognizer inner, RecordingStatistics statistics) : ISpeechRecognizer
    {
        public async Task<string> Recognize(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
        {
            long started = Stopwatch.GetTimestamp();
            string text = await inner.Recognize(samples, cancellationToken);
            statistics.AddRecognition(Stopwatch.GetElapsedTime(started));
            return text;
        }
    }

    /// <summary>
    /// Число событий пробуждения: рядом с подписчиком, который пишет их в файл.
    /// </summary>
    private sealed class WakeCounter(RecordingStatistics statistics) : IDomainEventSubscriber<SessionWakeRequested>
    {
        public Task Handle(SessionWakeRequested domainEvent, CancellationToken cancellationToken)
        {
            statistics.AddWake();
            return Task.CompletedTask;
        }
    }
}
