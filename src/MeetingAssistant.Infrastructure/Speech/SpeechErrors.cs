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

    public const string RefineThreadsInvalidCode = "Speech.RefineThreadsInvalid";

    public static Error RefineThreadsInvalid(int threads) =>
        new(RefineThreadsInvalidCode,
            $"Число потоков уточнения — от 1, задано {threads}: {SpeechOptions.Section}:{nameof(SpeechOptions.RefineThreads)}.");
}
