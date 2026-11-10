using System.Buffers;
using System.Globalization;
using System.Text;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Domain.Meetings;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Каталог текущей встречи процесса: его создаёт хранилище, в него пишут файлы встречи.
/// </summary>
internal sealed class MeetingDirectory
{
    private string? _path;

    public string Path => _path ?? throw new InvalidOperationException("Каталог встречи ещё не создан.");

    public void Use(string path) => _path = path;
}

/// <summary>
/// Создаёт каталог встречи <c>&lt;корень&gt;/&lt;дата&gt;_&lt;время&gt;_&lt;имя&gt;</c> по местному времени начала.
/// </summary>
internal sealed class MeetingFolderStorage(
    IOptions<StorageOptions> storageOptions,
    MeetingDirectory meetingDirectory,
    TimeProvider timeProvider) : IMeetingStorage
{
    public Task Create(Meeting meeting, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(storageOptions.Value.MeetingsRoot);
        DateTimeOffset localStart = TimeZoneInfo.ConvertTime(meeting.StartedAt, timeProvider.LocalTimeZone);
        string folder = $"{localStart.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}_{MeetingFolderName.From(meeting.Name)}";

        string path = Path.Combine(root, folder);

        for (int copy = 2; Directory.Exists(path); copy++)
        {
            path = Path.Combine(root, $"{folder}-{copy}");
        }

        Directory.CreateDirectory(path);
        meetingDirectory.Use(path);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Имя встречи в имени каталога: одно правило для Windows и Linux.
/// </summary>
/// <remarks>
/// Разделители путей заменяются, а перед именем всегда стоит дата, поэтому имя не выводит каталог за корень.
/// </remarks>
internal static class MeetingFolderName
{
    public const int MaxLength = 60;

    private const char Replacement = '_';

    // Символы, запрещённые в именах файлов Windows; на Linux из них запрещён только '/'.
    private static readonly SearchValues<char> Forbidden = SearchValues.Create("<>:\"/\\|?*");

    public static string From(string name)
    {
        var folder = new StringBuilder(name.Length);

        foreach (char symbol in name)
        {
            folder.Append(char.IsControl(symbol) || Forbidden.Contains(symbol) ? Replacement : symbol);
        }

        if (folder.Length > MaxLength)
        {
            // Суррогатная пара не разрезается: половина символа — недопустимое имя.
            folder.Length = char.IsHighSurrogate(folder[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        }

        // Windows срезает точки и пробелы в конце имени сам, и каталог открывался бы не под тем именем.
        return folder.ToString().TrimEnd('.', ' ');
    }
}
