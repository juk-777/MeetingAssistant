using System.Runtime.CompilerServices;
using MeetingAssistant.Infrastructure.Speech;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Speech;

/// <summary>
/// Модели репозитория (<c>models/</c>, вне git) и запись-образец из архива модели.
/// </summary>
/// <remarks>
/// Нет моделей — тест падает с командой скачивания, а не пропускается: пропуск выглядел бы зелёным.
/// </remarks>
internal static class Models
{
    public static string Directory { get; } = Path.GetFullPath(Path.Combine(RepositoryRoot(), "models"));

    /// <summary>
    /// Образец речи из архива GigaAM: приходит вместе с моделью и записей встреч не содержит.
    /// </summary>
    public static string ExampleWav => Path.Combine(Directory, SpeechModels.RecognizerDirectory, "test_wavs", "example.wav");

    /// <summary>
    /// Whisper tiny: проверяет связку с Whisper.net, а не качество.
    /// </summary>
    public static string WhisperTiny => Path.Combine(Directory, "ggml-tiny.bin");

    public static void EnsureWhisperTinyPresent()
    {
        if (!File.Exists(WhisperTiny))
        {
            Assert.Fail($"Нет модели {WhisperTiny}.\n{SpeechModels.DownloadHint}");
        }
    }

    public static void EnsurePresent()
    {
        IReadOnlyList<string> missing = SpeechModels.Missing(Directory);

        if (missing.Count > 0 || !File.Exists(ExampleWav))
        {
            Assert.Fail($"Нет моделей в {Directory}: {string.Join(", ", missing)}.\n{SpeechModels.DownloadHint}");
        }
    }

    /// <summary>
    /// Образец целиком: 16 кГц, моно, float.
    /// </summary>
    public static float[] ReadExample()
    {
        EnsurePresent();

        using var reader = new WaveFileReader(ExampleWav);
        ISampleProvider samples = reader.ToSampleProvider();
        var all = new float[reader.SampleCount];
        int read = samples.Read(all);

        return all[..read];
    }

    // Путь исходника, а не сборки: из WSL сборка уходит из дерева репозитория.
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.Combine(
            Path.GetDirectoryName(thisFile) ?? throw new InvalidOperationException($"Нет каталога у {thisFile}"),
            "..",
            "..",
            "..");
}
