using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Отказы хранилища встреч.
/// </summary>
public static class StorageErrors
{
    public const string MeetingsRootNotAbsoluteCode = "Storage.MeetingsRootNotAbsolute";

    public static Error MeetingsRootNotAbsolute(string root) =>
        new(
            MeetingsRootNotAbsoluteCode,
            $"Корень встреч «{root}» — не абсолютный путь: встреча легла бы туда, где её не найдёт скилл /meeting. " +
            $"Задайте его переменной окружения пользователя {StorageOptions.Section}__{nameof(StorageOptions.MeetingsRoot)}, " +
            "тот же путь — скиллу (MEETINGS_ROOT).");
}
