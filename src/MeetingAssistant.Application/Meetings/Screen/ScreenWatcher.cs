using MeetingAssistant.Domain.Screens;
using Microsoft.Extensions.Logging;

namespace MeetingAssistant.Application.Meetings.Screen;

/// <summary>
/// Снимок экрана с шагом <see cref="ScreenChangePolicy.CaptureInterval"/>: сетка → правило смены → запись смены.
/// </summary>
/// <remarks>
/// Агрегат встречи и очередь фраз снимков не видят: смену экрана ни с чем в транскрипте не связывают, и снимок
/// не ждёт распознавания фразы. Время смены — по часам от начала встречи, а не по звуку. Сбой снимка обычно длится
/// (экран заблокирован), поэтому в лог идёт первый сбой подряд и возврат снимков, а не каждая секунда.
/// </remarks>
internal sealed partial class ScreenWatcher(
    ICurrentMeeting currentMeeting,
    IScreenWriter writer,
    TimeProvider timeProvider,
    ILogger<ScreenWatcher> logger) : IScreenWatcher
{
    public async Task Watch(IScreenSource source, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = currentMeeting.Meeting?.StartedAt ?? timeProvider.GetUtcNow();
        var policy = new ScreenChangePolicy();
        bool failing = false;
        using var timer = new PeriodicTimer(ScreenChangePolicy.CaptureInterval, timeProvider);

        try
        {
            do
            {
                await Observe(timeProvider.GetUtcNow() - startedAt);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Конец записи — конец наблюдения.
        }

        async Task Observe(TimeSpan at)
        {
            try
            {
                ScreenFrame frame = source.Capture();

                if (failing)
                {
                    failing = false;
                    LogScreenRestored(at);
                }

                if (policy.Observe(ScreenGrid.FromRgb(frame.Width, frame.Height, frame.Rgb.Span), at))
                {
                    await writer.Write(frame, at, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Экран — дополнение к звуку: сбой снимка не повод останавливать запись встречи.
                if (!failing)
                {
                    failing = true;
                    LogScreenFailed(exception, at);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Снимок экрана снова идёт с {At}")]
    private partial void LogScreenRestored(TimeSpan at);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Снимок экрана на {At} не записан; следующие сбои подряд в лог не идут")]
    private partial void LogScreenFailed(Exception exception, TimeSpan at);
}
