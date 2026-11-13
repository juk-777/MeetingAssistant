using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Где лежат модели и сколько потоков отдать распознаванию: различается по машинам.
/// </summary>
public sealed class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>
    /// Каталог моделей; относительный путь — от текущего каталога процесса.
    /// </summary>
    public required string ModelsDirectory { get; init; }

    public required int RecognizerThreads { get; init; }
}

/// <summary>
/// Файлы моделей в каталоге моделей и откуда их взять.
/// </summary>
internal static class SpeechModels
{
    public const string VadFile = "silero_vad.onnx";
    public const string RecognizerDirectory = "sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16";
    public const string RecognizerModelFile = "model.int8.onnx";
    public const string RecognizerTokensFile = "tokens.txt";

    public const string DownloadHint =
        "Модели ставит установка приложения (install.ps1, README) — повторить её; в репозитории — ./scripts/download-models.sh.";

    public static string Vad(string modelsDirectory) => Path.Combine(Path.GetFullPath(modelsDirectory), VadFile);

    public static string RecognizerModel(string modelsDirectory) =>
        Path.Combine(Path.GetFullPath(modelsDirectory), RecognizerDirectory, RecognizerModelFile);

    public static string RecognizerTokens(string modelsDirectory) =>
        Path.Combine(Path.GetFullPath(modelsDirectory), RecognizerDirectory, RecognizerTokensFile);

    /// <summary>
    /// Whisper turbo — только для уточнения встречи: без него запись идёт, поэтому на старте он не проверяется.
    /// </summary>
    public static string Whisper(string modelsDirectory) => Path.Combine(Path.GetFullPath(modelsDirectory), WhisperFile);

    public const string WhisperFile = "ggml-large-v3-turbo-q5_0.bin";

    public static IReadOnlyList<string> Missing(string modelsDirectory) =>
    [
        .. new[] { Vad(modelsDirectory), RecognizerModel(modelsDirectory), RecognizerTokens(modelsDirectory) }
            .Where(path => !File.Exists(path)),
    ];
}

/// <summary>
/// Отказ на старте, если нет моделей: иначе встреча началась бы и упала на первой фразе.
/// </summary>
internal sealed class SpeechOptionsValidator : IValidateOptions<SpeechOptions>
{
    public ValidateOptionsResult Validate(string? name, SpeechOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            return ValidateOptionsResult.Fail(
                $"Не задан каталог моделей: {SpeechOptions.Section}:{nameof(SpeechOptions.ModelsDirectory)}.");
        }

        if (options.RecognizerThreads < 1)
        {
            return ValidateOptionsResult.Fail(
                $"Число потоков распознавания — от 1: {SpeechOptions.Section}:{nameof(SpeechOptions.RecognizerThreads)}.");
        }

        IReadOnlyList<string> missing = SpeechModels.Missing(options.ModelsDirectory);

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Нет файлов моделей: {string.Join(", ", missing)}.\n{SpeechModels.DownloadHint}");
    }
}
