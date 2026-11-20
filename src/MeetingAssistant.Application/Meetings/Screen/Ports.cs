namespace MeetingAssistant.Application.Meetings.Screen;

/// <summary>
/// Снимок монитора.
/// </summary>
/// <param name="Width">Ширина в точках.</param>
/// <param name="Height">Высота в точках.</param>
/// <param name="Rgb">Точки построчно сверху вниз, по три байта: красный, зелёный, синий.</param>
public sealed record ScreenFrame(int Width, int Height, ReadOnlyMemory<byte> Rgb);

/// <summary>
/// Монитор, который снимается вместе со звуком встречи.
/// </summary>
public interface IScreenSource
{
    ScreenFrame Capture();
}

/// <summary>
/// Запись смены экрана в каталог встречи: снимок, затем строка журнала экрана.
/// </summary>
public interface IScreenWriter
{
    /// <param name="frame">Снимок смены.</param>
    /// <param name="at">Время смены от начала встречи.</param>
    /// <param name="cancellationToken">Отмена записи.</param>
    Task Write(ScreenFrame frame, TimeSpan at, CancellationToken cancellationToken);
}

/// <summary>
/// Снимает экран, пока идёт запись встречи, и записывает его смены.
/// </summary>
public interface IScreenWatcher
{
    /// <summary>
    /// Снимает экран до отмены; сбой снимка или записи не останавливает наблюдение.
    /// </summary>
    Task Watch(IScreenSource source, CancellationToken cancellationToken);
}
