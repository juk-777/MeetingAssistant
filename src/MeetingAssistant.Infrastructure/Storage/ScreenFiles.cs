using System.Globalization;
using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.Infrastructure.Screen;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Смены экрана в каталоге встречи: снимок в <c>screen/</c>, затем строка в <c>screen.log</c>.
/// </summary>
/// <remarks>
/// Строка пишется после закрытого файла снимка: окно подсказок, увидев строку, читает снимок целиком.
/// </remarks>
internal sealed class ScreenFiles(MeetingDirectory meetingDirectory, TimeProvider timeProvider) : IScreenWriter
{
    public const string DirectoryName = "screen";
    public const string LogFileName = "screen.log";

    public async Task Write(ScreenFrame frame, TimeSpan at, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(meetingDirectory.Path, DirectoryName);
        Directory.CreateDirectory(directory);

        string fileName = FileName(at);
        ScreenFrame image = ScreenImage.Downscale(frame);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, fileName),
            PngImage.Encode(image.Width, image.Height, image.Rgb.Span),
            cancellationToken);

        await MeetingFileLine.Append(
            Path.Combine(meetingDirectory.Path, LogFileName),
            $"{MeetingFileFormat.Epoch(timeProvider.GetUtcNow())}\t{fileName}",
            cancellationToken);
    }

    /// <summary>
    /// Имя снимка — время от начала встречи: <c>чч-мм-сс.png</c>.
    /// </summary>
    public static string FileName(TimeSpan at) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)at.TotalHours:D2}-{at.Minutes:D2}-{at.Seconds:D2}.png");
}
