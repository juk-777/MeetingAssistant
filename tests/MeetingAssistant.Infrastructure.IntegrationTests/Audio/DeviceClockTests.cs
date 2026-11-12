using MeetingAssistant.Infrastructure.Audio;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class DeviceClockTests
{
    [Fact]
    public void ToTimestamp_WhenFrequencyIs10MHz_KeepsValue()
    {
        DeviceClock.ToTimestamp(123_456_789, 10_000_000).ShouldBe(123_456_789);
    }

    [Fact]
    public void ToTimestamp_WhenFrequencyDiffers_ScalesToItsUnits()
    {
        // 2.5 с в единицах 100 нс на счётчике 3.579545 МГц (частота ACPI PM timer).
        DeviceClock.ToTimestamp(25_000_000, 3_579_545).ShouldBe(8_948_862);
    }

    [Fact]
    public void ToTimestamp_WhenMachineRunsForMonths_DoesNotOverflow()
    {
        // 200 суток работы: произведение на частоту 1 ГГц не помещается в long.
        long qpcPosition = TimeSpan.FromDays(200).Ticks;

        DeviceClock.ToTimestamp(qpcPosition, 1_000_000_000).ShouldBe(TimeSpan.FromDays(200).Ticks * 100);
    }
}
