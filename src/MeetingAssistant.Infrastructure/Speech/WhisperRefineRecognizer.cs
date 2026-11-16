using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.SharedKernel;
using Whisper.net;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Whisper через Whisper.net: русский, кусок без контекста предыдущего, потоки — из настройки, но не больше половины
/// логических процессоров.
/// </summary>
/// <remarks>
/// Без контекста — опора на прошлый вывод уводит модель в петлю повторов на записи с провалами. Модель ставит
/// установка рядом с остальными; грузится она в <see cref="Prepare"/>, а не в конструкторе: сначала проверяется
/// встреча, и отказ по ней не ждёт загрузки 574 МБ. Вызывается одним потоком сценария.
/// </remarks>
/// <param name="modelPath">Файл модели ggml.</param>
/// <param name="configuredThreads">Настройка <c>Speech:RefineThreads</c>.</param>
internal sealed class WhisperRefineRecognizer(string modelPath, int configuredThreads)
    : IRefineRecognizer, IAsyncDisposable
{
    public const string Language = "ru";

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    /// <summary>
    /// Потоки Whisper: уточнение идёт после встречи и не держит больше половины машины, даже если настройка выше.
    /// </summary>
    public int Threads => ThreadsFor(configuredThreads, Environment.ProcessorCount);

    public static int ThreadsFor(int configuredThreads, int processorCount) =>
        Math.Min(configuredThreads, Math.Max(1, processorCount / 2));

    /// <summary>
    /// Окно кодировщика Whisper: кусок короче всё равно считается как 30 с.
    /// </summary>
    public TimeSpan Window => TimeSpan.FromSeconds(30);

    public Task<Result> Prepare(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            return Task.FromResult(Result.Success());
        }

        if (configuredThreads < 1)
        {
            return Task.FromResult(Result.Failure(SpeechErrors.RefineThreadsInvalid(configuredThreads)));
        }

        if (!File.Exists(modelPath))
        {
            return Task.FromResult(Result.Failure(SpeechErrors.ModelMissing(modelPath)));
        }

        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage(Language)
            .WithNoContext()
            .WithThreads(Threads)
            .Build();

        return Task.FromResult(Result.Success());
    }

    public async Task<IReadOnlyList<RecognizedSegment>> Recognize(
        ReadOnlyMemory<float> samples,
        CancellationToken cancellationToken)
    {
        WhisperProcessor processor = _processor
            ?? throw new InvalidOperationException("Модель Whisper не подготовлена: сначала Prepare.");
        var segments = new List<RecognizedSegment>();

        await foreach (SegmentData segment in processor.ProcessAsync(samples, cancellationToken))
        {
            segments.Add(new RecognizedSegment(segment.Start, segment.End, segment.Text.Trim()));
        }

        return segments;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }

        _factory?.Dispose();
    }
}
