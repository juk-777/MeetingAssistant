using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Отказы распознавания: модель не получена.
/// </summary>
public static class SpeechErrors
{
    public const string ModelDownloadFailedCode = "Speech.ModelDownloadFailed";
    public const string ModelChecksumMismatchCode = "Speech.ModelChecksumMismatch";

    public static Error ModelDownloadFailed(Uri source, string reason) =>
        new(ModelDownloadFailedCode, $"Модель не скачалась ({source}): {reason}. Повторите — загрузка начнётся заново.");

    public static Error ModelChecksumMismatch(Uri source) =>
        new(
            ModelChecksumMismatchCode,
            $"Контрольная сумма модели ({source}) не совпала: файл повреждён или подменён, на место он не положен.");
}
