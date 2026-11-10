using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Start;
using MeetingAssistant.Application.Meetings.Stop;
using MeetingAssistant.Application.UnitTests.Meetings;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace MeetingAssistant.Application.UnitTests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_WhenScanned_RegistersExactlyOneHandlerForEveryCommand()
    {
        using var harness = new ScenarioHarness();
        Type[] commandTypes = [.. typeof(ICommand).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => type.GetInterfaces().Any(IsCommand))];

        commandTypes.ShouldNotBeEmpty();

        foreach (Type commandType in commandTypes)
        {
            harness.ResolveAll(HandlerTypeOf(commandType)).Count.ShouldBe(1, commandType.Name);
        }
    }

    [Fact]
    public async Task AddApplication_WhenCommandWithoutValueFails_LogsWarningWithErrorCode()
    {
        using var harness = new ScenarioHarness();

        Result result = await harness.Handler<StopMeeting>().Handle(new StopMeeting(), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        AssertWarningLogged(harness, MeetingScenarioErrors.NotRunning.Code);
    }

    [Fact]
    public async Task AddApplication_WhenCommandWithValueFails_LogsWarningWithErrorCode()
    {
        using var harness = new ScenarioHarness();

        Result<Guid> result = await harness.Handler<StartMeeting, Guid>()
            .Handle(new StartMeeting(" ", []), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        AssertWarningLogged(harness, MeetingScenarioErrors.NameRequired.Code);
    }

    private static void AssertWarningLogged(ScenarioHarness harness, string errorCode) =>
        harness.Log.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(errorCode));

    private static bool IsCommand(Type type) =>
        type == typeof(ICommand) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICommand<>));

    private static Type HandlerTypeOf(Type commandType)
    {
        Type commandInterface = commandType.GetInterfaces().First(IsCommand);

        return commandInterface == typeof(ICommand)
            ? typeof(ICommandHandler<>).MakeGenericType(commandType)
            : typeof(ICommandHandler<,>).MakeGenericType(commandType, commandInterface.GetGenericArguments()[0]);
    }
}
