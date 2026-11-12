namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Время пакета WASAPI на часах встречи.
/// </summary>
internal static class DeviceClock
{
    private const long HundredNanosecondsPerSecond = 10_000_000;

    /// <summary>
    /// Переводит <c>QPCPosition</c> пакета (счётчик производительности в единицах 100 нс) в отметку
    /// <see cref="TimeProvider.System"/>: она тоже счётчик производительности, но в его собственной частоте.
    /// </summary>
    /// <param name="qpcPosition">Время записи первого кадра в единицах 100 нс.</param>
    /// <param name="frequency">Частота отметок, <see cref="System.Diagnostics.Stopwatch.Frequency"/>.</param>
    public static long ToTimestamp(long qpcPosition, long frequency) =>
        (long)((Int128)qpcPosition * frequency / HundredNanosecondsPerSecond);
}
