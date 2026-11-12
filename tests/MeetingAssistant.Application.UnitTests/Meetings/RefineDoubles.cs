using System.Runtime.CompilerServices;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Application.UnitTests.Meetings;

/// <summary>
/// Второй транскрипт в памяти: последний начатый черновик.
/// </summary>
internal sealed class DraftTranscripts : IRefinedTranscripts
{
    public DraftTranscript? Draft { get; private set; }

    /// <summary>
    /// Дозапись строки ждёт эту задачу, если она задана.
    /// </summary>
    public Func<Utterance, Task>? OnAppend { get; set; }

    public Task<IRefinedTranscriptDraft> Begin(string directory, CancellationToken cancellationToken)
    {
        Draft = new DraftTranscript(directory, OnAppend);
        return Task.FromResult<IRefinedTranscriptDraft>(Draft);
    }
}

internal sealed class DraftTranscript(string directory, Func<Utterance, Task>? onAppend) : IRefinedTranscriptDraft
{
    private readonly List<Utterance> _lines = [];

    public string Directory { get; } = directory;

    public IReadOnlyList<Utterance> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public bool Completed { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>
    /// Брошен: освобождён без завершения.
    /// </summary>
    public bool Abandoned => Disposed && !Completed;

    public async Task Append(Utterance utterance, CancellationToken cancellationToken)
    {
        if (onAppend is not null)
        {
            await onAppend(utterance);
        }

        lock (_lines)
        {
            _lines.Add(utterance);
        }
    }

    public Task Complete(CancellationToken cancellationToken)
    {
        Completed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Источник с заданными кусками, считающий, сколько кусков у него уже взяли.
/// </summary>
internal sealed class CountingSource(IReadOnlyList<AudioChunk> chunks) : IAudioSource
{
    private int _taken;

    public int Taken => Volatile.Read(ref _taken);

    public async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (AudioChunk chunk in chunks)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _taken);
            yield return chunk;
        }
    }
}
