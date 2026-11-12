using System.Security.Cryptography;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Logging;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Файл модели Whisper: откуда скачать и какая у него SHA-256.
/// </summary>
/// <param name="FileName">Имя файла в каталоге моделей.</param>
/// <param name="Source">Адрес, закреплённый на ревизии: по <c>main</c> файл мог бы смениться и разойтись с суммой.</param>
/// <param name="Sha256">SHA-256 файла, строчными шестнадцатеричными.</param>
internal sealed record WhisperModelSource(string FileName, Uri Source, string Sha256)
{
    public static readonly WhisperModelSource Turbo = new(
        "ggml-large-v3-turbo-q5_0.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin"),
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2");
}

/// <summary>
/// Кладёт модель Whisper в каталог моделей, если её там нет: скачанное сверяется с суммой до того, как лечь на место.
/// </summary>
/// <remarks>
/// Загрузка идёт в <c>.part</c> и переименовывается только после сверки: оборванная или подменённая загрузка не
/// выглядит моделью. Модель на месте не сверяется: её положила эта же сверка.
/// </remarks>
internal sealed partial class WhisperModelFile(
    string modelsDirectory,
    WhisperModelSource source,
    HttpClient httpClient,
    ILogger<WhisperModelFile> logger)
{
    public const string DraftSuffix = ".part";

    private const int ReportEveryPercent = 10;
    private const double BytesPerMegabyte = 1024 * 1024;

    private readonly string _directory = System.IO.Path.GetFullPath(modelsDirectory);

    public string Path => System.IO.Path.Combine(_directory, source.FileName);

    public async Task<Result> Ensure(CancellationToken cancellationToken)
    {
        if (File.Exists(Path))
        {
            return Result.Success();
        }

        string draft = Path + DraftSuffix;

        try
        {
            string sha256 = await Download(draft, cancellationToken);

            if (!string.Equals(sha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(draft);
                return Result.Failure(SpeechErrors.ModelChecksumMismatch(source.Source));
            }

            File.Move(draft, Path);
            return Result.Success();
        }
        catch (HttpRequestException exception)
        {
            File.Delete(draft);
            return Result.Failure(SpeechErrors.ModelDownloadFailed(source.Source, exception.Message));
        }
        catch
        {
            File.Delete(draft);
            throw;
        }
    }

    /// <returns>SHA-256 скачанного.</returns>
    private async Task<string> Download(string draft, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);

        using HttpResponseMessage response = await httpClient.GetAsync(
            source.Source,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        LogDownloading(logger, source.Source, (total ?? 0) / BytesPerMegabyte);

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(draft, FileMode.Create, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        long received = 0;
        int reported = 0;
        int read;

        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            if (total is > 0 && received * 100 / total.Value >= reported + ReportEveryPercent)
            {
                reported = (int)(received * 100 / total.Value) / ReportEveryPercent * ReportEveryPercent;
                LogProgress(logger, reported);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Загрузка модели Whisper ({Megabytes:F0} МБ): {Source}")]
    private static partial void LogDownloading(ILogger logger, Uri source, double megabytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Модель Whisper: {Percent} %")]
    private static partial void LogProgress(ILogger logger, int percent);
}
