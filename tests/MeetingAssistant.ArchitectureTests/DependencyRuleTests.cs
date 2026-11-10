using System.Reflection;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using NetArchTest.Rules;
using Shouldly;

namespace MeetingAssistant.ArchitectureTests;

/// <summary>
/// Зависимости слоёв направлены внутрь; внешние библиотеки звука и распознавания видит только Infrastructure.
/// </summary>
public sealed class DependencyRuleTests
{
    private const string ApplicationNamespace = "MeetingAssistant.Application";
    private const string InfrastructureNamespace = "MeetingAssistant.Infrastructure";
    private const string CliNamespace = "MeetingAssistant.Cli";
    private const string DomainNamespace = "MeetingAssistant.Domain";

    private static readonly string[] ExternalAudioAndSpeech = ["NAudio", "SherpaOnnx"];

    private static readonly Assembly SharedKernelAssembly = typeof(Entity<>).Assembly;
    private static readonly Assembly DomainAssembly = typeof(Channel).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ICommand).Assembly;

    [Fact]
    public void SharedKernel_WhenInspected_DependsOnNoOtherLayer()
    {
        AssertNoDependency(SharedKernelAssembly, DomainNamespace, ApplicationNamespace, InfrastructureNamespace, CliNamespace);
    }

    [Fact]
    public void Domain_WhenInspected_DependsOnlyOnSharedKernel()
    {
        AssertNoDependency(DomainAssembly, [ApplicationNamespace, InfrastructureNamespace, CliNamespace, .. ExternalAudioAndSpeech]);
    }

    [Fact]
    public void Application_WhenInspected_DoesNotKnowInfrastructureOrEntryPoint()
    {
        AssertNoDependency(ApplicationAssembly, [InfrastructureNamespace, CliNamespace, .. ExternalAudioAndSpeech]);
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        NetArchTest.Rules.TestResult result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{assembly.GetName().Name} зависит от запрещённого: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
