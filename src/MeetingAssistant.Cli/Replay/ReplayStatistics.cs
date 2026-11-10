using System.Diagnostics;
using System.Globalization;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Cli.Replay;

/// <summary>
/// Итог прогона: из него берутся цифры замеров (<c>docs/measurements.md</c>).
/// </summary>
internal sealed class ReplayStatistics
{
    private readonly List<TimeSpan> _recognitions = [];
    private int _wakes;

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ReplayStatistics>();
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

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Строк транскрипта {utterances}, событий {_wakes}; распознавание: {recognition}; прогон {elapsed.TotalSeconds:F1} с");
    }

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Время распознавания каждой фразы.
    /// </summary>
    private sealed class TimedSpeechRecognizer(ISpeechRecognizer inner, ReplayStatistics statistics) : ISpeechRecognizer
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
    private sealed class WakeCounter(ReplayStatistics statistics) : IDomainEventSubscriber<SessionWakeRequested>
    {
        public Task Handle(SessionWakeRequested domainEvent, CancellationToken cancellationToken)
        {
            statistics.AddWake();
            return Task.CompletedTask;
        }
    }
}
