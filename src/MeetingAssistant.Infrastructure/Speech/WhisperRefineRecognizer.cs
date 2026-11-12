using System.Text;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.SharedKernel;
using Whisper.net;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Whisper через Whisper.net: русский, фраза без контекста предыдущей, половина логических процессоров.
/// </summary>
/// <remarks>
/// Так turbo мерили (<c>docs/measurements.md</c>). Модель грузится в <see cref="Prepare"/>, а не в конструкторе:
/// сначала проверяется встреча, и отказ по ней не ждёт загрузки 574 МБ. Вызывается одним потоком сценария.
/// </remarks>
internal sealed class WhisperRefineRecognizer(WhisperModelFile modelFile) : IRefineRecognizer, IAsyncDisposable
{
    public const string Language = "ru";

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public static int Threads => Math.Max(1, Environment.ProcessorCount / 2);

    public async Task<Result> Prepare(CancellationToken cancellationToken)
    {
        Result ensured = await modelFile.Ensure(cancellationToken);

        if (ensured.IsFailure || _processor is not null)
        {
            return ensured;
        }

        _factory = WhisperFactory.FromPath(modelFile.Path);
        _processor = _factory.CreateBuilder()
            .WithLanguage(Language)
            .WithNoContext()
            .WithThreads(Threads)
            .Build();

        return Result.Success();
    }

    public async Task<string> Recognize(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        WhisperProcessor processor = _processor
            ?? throw new InvalidOperationException("Модель Whisper не подготовлена: сначала Prepare.");
        var text = new StringBuilder();

        await foreach (SegmentData segment in processor.ProcessAsync(samples, cancellationToken))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
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
