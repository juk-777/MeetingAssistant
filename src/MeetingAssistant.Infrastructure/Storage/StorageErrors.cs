using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Отказы хранилища встреч.
/// </summary>
public static class StorageErrors
{
    public const string MeetingsRootNotAbsoluteCode = "Storage.MeetingsRootNotAbsolute";
    public const string MeetingNotFoundCode = "Storage.MeetingNotFound";
    public const string AudioNotFoundCode = "Storage.AudioNotFound";
    public const string RecordingNotFinishedCode = "Storage.RecordingNotFinished";

    public static Error MeetingNotFound(string directory) =>
        new(MeetingNotFoundCode, $"Нет каталога встречи: {directory}.");

    public static Error AudioNotFound(string path) =>
        new(AudioNotFoundCode, $"Нет звука встречи: {path}.");

    public static Error RecordingNotFinished(string directory) =>
        new(
            RecordingNotFinishedCode,
            $"Запись встречи {directory} не закончена: в {EventsFile.FileName} нет «{MeetingFileFormat.EndOfRecording}». " +
            "Остановите запись (Ctrl+C в окне meeting) и повторите; оборванную аварийно встречу уточнить нельзя.");

    public static Error MeetingsRootNotAbsolute(string root) =>
        new(
            MeetingsRootNotAbsoluteCode,
            $"Корень встреч «{root}» — не абсолютный путь: встреча легла бы туда, где её не найдёт скилл /meeting. " +
            $"Задайте его переменной окружения пользователя {StorageOptions.Section}__{nameof(StorageOptions.MeetingsRoot)} " +
            "(её задаёт scripts/install.ps1, скилл читает её же).");
}
