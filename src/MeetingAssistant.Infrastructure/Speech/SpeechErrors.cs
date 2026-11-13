using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Отказы распознавания.
/// </summary>
public static class SpeechErrors
{
    public const string ModelMissingCode = "Speech.ModelMissing";

    public static Error ModelMissing(string path) =>
        new(ModelMissingCode, $"Нет модели {path}.\n{SpeechModels.DownloadHint}");
}
