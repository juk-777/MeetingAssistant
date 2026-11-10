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
        "Модели — docs/measurements.md, «Модели»: в каталоге моделей выполнить\n" +
        "  base=https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models\n" +
        $"  curl -sSLO $base/{RecognizerDirectory}.tar.bz2\n" +
        $"  curl -sSLO $base/{VadFile}\n" +
        "и распаковать архив там же (без bzip2 — способ из того же раздела).";

    public static string Vad(string modelsDirectory) => Path.Combine(Path.GetFullPath(modelsDirectory), VadFile);

    public static string RecognizerModel(string modelsDirectory) =>
        Path.Combine(Path.GetFullPath(modelsDirectory), RecognizerDirectory, RecognizerModelFile);

    public static string RecognizerTokens(string modelsDirectory) =>
        Path.Combine(Path.GetFullPath(modelsDirectory), RecognizerDirectory, RecognizerTokensFile);

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
