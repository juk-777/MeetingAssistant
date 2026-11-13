using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Domain.Meetings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeetingAssistant.Application.UnitTests.Meetings;

/// <summary>
/// Сценарии через ту же регистрацию, что в бою (<c>AddApplication</c>), с двойниками портов.
/// </summary>
internal sealed class ScenarioHarness : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public ScenarioHarness()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<ICurrentMeeting>(CurrentMeeting);
        services.AddSingleton(Storage);
        services.AddSingleton(Recognizer);
        services.AddSingleton(Transcript);
        services.AddSingleton(WakeEvents);
        services.AddSingleton<IVoiceActivityDetectorFactory>(Detectors);
        services.AddSingleton(RecordedMeetings);
        services.AddSingleton(RefineRecognizer);
        services.AddSingleton<IRefinedTranscripts>(RefinedTranscripts);
        services.AddApplication();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    public LogSink Log { get; } = new();

    public FakeTimeProvider Time { get; } = new(Now);

    public InMemoryCurrentMeeting CurrentMeeting { get; } = new();

    public IMeetingStorage Storage { get; } = Substitute.For<IMeetingStorage>();

    public ISpeechRecognizer Recognizer { get; } = Substitute.For<ISpeechRecognizer>();

    public ITranscriptWriter Transcript { get; } = Substitute.For<ITranscriptWriter>();

    public IWakeEventWriter WakeEvents { get; } = Substitute.For<IWakeEventWriter>();

    public HoldingDetectors Detectors { get; } = new();

    public IRecordedMeetings RecordedMeetings { get; } = Substitute.For<IRecordedMeetings>();

    public IRefineRecognizer RefineRecognizer { get; } = Substitute.For<IRefineRecognizer>();

    public DraftTranscripts RefinedTranscripts { get; } = new();

    public IMeetingRecorder Recorder() => _provider.GetRequiredService<IMeetingRecorder>();

    public ICommandHandler<TCommand> Handler<TCommand>()
        where TCommand : ICommand =>
        _provider.GetRequiredService<ICommandHandler<TCommand>>();

    public ICommandHandler<TCommand, TResponse> Handler<TCommand, TResponse>()
        where TCommand : ICommand<TResponse>
        where TResponse : notnull =>
        _provider.GetRequiredService<ICommandHandler<TCommand, TResponse>>();

    public IReadOnlyList<object?> ResolveAll(Type serviceType) => [.. _provider.GetServices(serviceType)];

    public Meeting RunningMeeting(Glossary? glossary = null)
    {
        var meeting = Meeting.Start("Встреча", glossary ?? Glossary.Empty, Now);
        CurrentMeeting.Meeting = meeting;
        return meeting;
    }

    public void RecognizerReturns(string text) =>
        Recognizer.Recognize(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<CancellationToken>()).Returns(text);

    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// Записи журнала всех логгеров обвязки.
/// </summary>
internal sealed class LogSink
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public void Add(LogLevel level, string message)
    {
        lock (_entries)
        {
            _entries.Add((level, message));
        }
    }
}

/// <summary>
/// Логгер, складывающий записи в <see cref="LogSink"/>.
/// </summary>
internal sealed class CapturingLogger<T>(LogSink sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        sink.Add(logLevel, formatter(state, exception));
}

/// <summary>
/// Текущая встреча в памяти теста.
/// </summary>
internal sealed class InMemoryCurrentMeeting : ICurrentMeeting
{
    public Meeting? Meeting { get; set; }

    public Task Set(Meeting meeting, CancellationToken cancellationToken)
    {
        Meeting = meeting;
        return Task.CompletedTask;
    }
}
