using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.SharedKernel;
using Whisper.net;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Whisper через Whisper.net: русский, кусок без контекста предыдущего, половина логических процессоров.
/// </summary>
/// <remarks>
/// Без контекста — опора на прошлый вывод уводит модель в петлю повторов на записи с провалами. Модель ставит
/// установка рядом с остальными; грузится она в <see cref="Prepare"/>, а не в конструкторе: сначала проверяется
/// встреча, и отказ по ней не ждёт загрузки 574 МБ. Вызывается одним потоком сценария.
/// </remarks>
internal sealed class WhisperRefineRecognizer(string modelPath) : IRefineRecognizer, IAsyncDisposable
{
    public const string Language = "ru";

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public static int Threads => Math.Max(1, Environment.ProcessorCount / 2);

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
