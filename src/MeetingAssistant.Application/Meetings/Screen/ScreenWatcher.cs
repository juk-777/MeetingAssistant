using MeetingAssistant.Domain.Screens;
using Microsoft.Extensions.Logging;

namespace MeetingAssistant.Application.Meetings.Screen;

/// <summary>
/// Снимок экрана с шагом <see cref="ScreenChangePolicy.CaptureInterval"/>: сетка → правило смены → запись смены.
/// </summary>
/// <remarks>
/// Агрегат встречи и очередь фраз снимков не видят: смену экрана ни с чем в транскрипте не связывают, и снимок
/// не ждёт распознавания фразы. Время смены — по часам от начала встречи, а не по звуку.
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
        using var timer = new PeriodicTimer(ScreenChangePolicy.CaptureInterval, timeProvider);

        try
        {
            do
            {
                await Observe(source, policy, timeProvider.GetUtcNow() - startedAt, cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Конец записи — конец наблюдения.
        }
    }

    private async Task Observe(IScreenSource source, ScreenChangePolicy policy, TimeSpan at, CancellationToken cancellationToken)
    {
        try
        {
            ScreenFrame frame = source.Capture();

            if (policy.Observe(ScreenGrid.FromRgb(frame.Width, frame.Height, frame.Rgb.Span), at))
            {
                await writer.Write(frame, at, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Экран — дополнение к звуку: сбой снимка не повод останавливать запись встречи.
            LogScreenFailed(exception, at);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Снимок экрана на {At} не записан")]
    private partial void LogScreenFailed(Exception exception, TimeSpan at);
}
