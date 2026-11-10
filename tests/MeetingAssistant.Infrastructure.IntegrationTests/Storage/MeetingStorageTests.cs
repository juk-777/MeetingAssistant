using System.Text.Json;
using MeetingAssistant.Infrastructure.Storage;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Storage;

public sealed class MeetingStorageTests : IDisposable
{
    private readonly StorageHarness _harness = new();

    [Fact]
    public async Task Start_WhenStarted_CreatesFolderUnderRootAndPointsCurrentJsonToIt()
    {
        Guid id = await _harness.Start("Обзор: план/итоги & О'Нил");

        string folder = _harness.MeetingFolder;
        folder.ShouldBe(Path.Combine(_harness.Root, "2026-01-01_10-00-00_Обзор_ план_итоги & О'Нил"));
        Directory.Exists(folder).ShouldBeTrue();

        string json = await File.ReadAllTextAsync(
            Path.Combine(_harness.Root, CurrentMeetingFile.FileName),
            TestContext.Current.CancellationToken);
        json.ShouldContain("\"name\": \"Обзор: план/итоги & О'Нил\"");

        using JsonDocument current = JsonDocument.Parse(json);
        current.RootElement.GetProperty("id").GetGuid().ShouldBe(id);
        current.RootElement.GetProperty("directory").GetString().ShouldBe(folder);
        current.RootElement.GetProperty("startedAt").GetDateTimeOffset().ShouldBe(StorageHarness.Now);
        Directory.GetFiles(_harness.Root).ShouldBe([Path.Combine(_harness.Root, CurrentMeetingFile.FileName)]);
    }

    [Theory]
    [InlineData("../../наружу")]
    [InlineData(@"..\..\наружу")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows")]
    [InlineData("..")]
    public async Task Start_WhenNameLooksLikePath_KeepsFolderDirectlyUnderRoot(string name)
    {
        await _harness.Start(name);

        Path.GetDirectoryName(_harness.MeetingFolder).ShouldBe(_harness.Root);
        Directory.Exists(_harness.MeetingFolder).ShouldBeTrue();
    }

    [Fact]
    public async Task Start_WhenFolderTaken_AddsCopyNumber()
    {
        await _harness.Start("Повтор");
        string first = _harness.MeetingFolder;
        await _harness.Stop();

        await _harness.Start("Повтор");

        _harness.MeetingFolder.ShouldBe(first + "-2");
        Directory.Exists(first).ShouldBeTrue();
    }

    [Fact]
    public async Task Start_WhenStartedAtIsUtc_NamesFolderByLocalTime()
    {
        _harness.Time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3"));

        await _harness.Start("Местное");

        Path.GetFileName(_harness.MeetingFolder).ShouldBe("2026-01-01_13-00-00_Местное");
    }

    public void Dispose() => _harness.Dispose();
}
