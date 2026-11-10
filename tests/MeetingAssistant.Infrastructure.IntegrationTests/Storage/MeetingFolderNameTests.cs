using MeetingAssistant.Infrastructure.Storage;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Storage;

public sealed class MeetingFolderNameTests
{
    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("строка\tс\nуправляющими", "строка_с_управляющими")]
    [InlineData("Итоги...", "Итоги")]
    [InlineData("Итоги . ", "Итоги")]
    [InlineData("Планёрка", "Планёрка")]
    public void From_WhenNameHasForbiddenParts_ReplacesOrTrimsThem(string name, string expected)
    {
        MeetingFolderName.From(name).ShouldBe(expected);
    }

    [Fact]
    public void From_WhenNameTooLong_CutsToLimit()
    {
        string name = new('я', MeetingFolderName.MaxLength + 1);

        MeetingFolderName.From(name).ShouldBe(new string('я', MeetingFolderName.MaxLength));
    }

    [Fact]
    public void From_WhenLimitFallsInsideSurrogatePair_DropsWholeSymbol()
    {
        string name = new string('я', MeetingFolderName.MaxLength - 1) + "😀";

        MeetingFolderName.From(name).ShouldBe(new string('я', MeetingFolderName.MaxLength - 1));
    }
}
