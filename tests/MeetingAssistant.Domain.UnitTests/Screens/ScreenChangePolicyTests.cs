using MeetingAssistant.Domain.Screens;
using MeetingAssistant.SharedKernel;
using Shouldly;

namespace MeetingAssistant.Domain.UnitTests.Screens;

public sealed class ScreenChangePolicyTests
{
    private const byte Background = 100;

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void Observe_WhenFirstFrame_ChangesAndSameFrameThenDoesNot()
    {
        ScreenChangePolicy policy = new();

        policy.Observe(Plain(), TimeSpan.Zero).ShouldBeTrue();

        for (int second = 1; second <= 30; second++)
        {
            policy.Observe(Plain(), second * Second).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(ScreenChangePolicy.MinChangedCells, true)]
    [InlineData(ScreenChangePolicy.MinChangedCells - 1, false)]
    public void Observe_WhenSettledChange_ChangesFromMinCells(int cells, bool changed)
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);
        ScreenGrid frame = WithCells(cells, Background + 50);

        policy.Observe(frame, at).ShouldBeFalse();
        policy.Observe(frame, at + ScreenChangePolicy.Settle).ShouldBe(changed);
    }

    [Fact]
    public void Observe_WhenChangeNotSettledYet_DoesNotChange()
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);
        ScreenGrid frame = WithCells(20, Background + 50);

        policy.Observe(frame, at).ShouldBeFalse();
        policy.Observe(frame, at + ScreenChangePolicy.Settle - TimeSpan.FromSeconds(0.1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ScreenChangePolicy.BrightnessThreshold, false)]
    [InlineData(ScreenChangePolicy.BrightnessThreshold + 1, true)]
    public void Observe_WhenBrightnessShifts_CountsOnlyAboveThreshold(int shift, bool changed)
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);
        ScreenGrid frame = WithCells(20, Background + shift);

        policy.Observe(frame, at);
        policy.Observe(frame, at + ScreenChangePolicy.Settle).ShouldBe(changed);
    }

    [Fact]
    public void Observe_WhenCellsChangeEveryFrame_NeverChanges()
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);

        for (int second = 0; second < 120; second++)
        {
            policy.Observe(Video(second), at + second * Second).ShouldBeFalse();
        }
    }

    [Fact]
    public void Observe_WhenSettledChangeBesideVideo_Changes()
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);
        bool changed = false;

        for (int second = 0; second <= ScreenChangePolicy.Settle.TotalSeconds; second++)
        {
            ScreenGrid frame = Video(second, settled: ScreenChangePolicy.MinChangedCells);
            changed = policy.Observe(frame, at + second * Second);
        }

        changed.ShouldBeTrue();
    }

    [Fact]
    public void Observe_WhenCellsChangedOftenRecently_IgnoresThemUntilWindowPasses()
    {
        // Видео в окне звонка: ячейки меняются несколько раз и встают — живые, пока изменения не уйдут из окна.
        ScreenChangePolicy policy = Watching(out TimeSpan at);

        for (int change = 0; change < ScreenChangePolicy.LivelyChanges; change++)
        {
            policy.Observe(WithCells(20, Background + 50 + change % 2 * 50), at + change * Second).ShouldBeFalse();
        }

        ScreenGrid settled = WithCells(20, Background + 50 + (ScreenChangePolicy.LivelyChanges - 1) % 2 * 50);
        TimeSpan oldestChange = at;
        policy.Observe(settled, at + 10 * Second).ShouldBeFalse();
        policy.Observe(settled, oldestChange + ScreenChangePolicy.LivelyWindow - TimeSpan.FromSeconds(0.1)).ShouldBeFalse();
        policy.Observe(settled, oldestChange + ScreenChangePolicy.LivelyWindow).ShouldBeTrue();
    }

    [Fact]
    public void Observe_WhenCellsChangedFewerTimesThanLively_ChangesAfterSettle()
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);
        int lastChange = ScreenChangePolicy.LivelyChanges - 2;

        for (int change = 0; change <= lastChange; change++)
        {
            policy.Observe(WithCells(20, Background + 50 + change % 2 * 50), at + change * Second);
        }

        ScreenGrid settled = WithCells(20, Background + 50 + lastChange % 2 * 50);
        policy.Observe(settled, at + lastChange * Second + ScreenChangePolicy.Settle).ShouldBeTrue();
    }

    [Fact]
    public void Observe_WhenChangeBeforeMinInterval_WaitsForIntervalThenChanges()
    {
        ScreenChangePolicy policy = new();
        policy.Observe(Plain(), TimeSpan.Zero);
        ScreenGrid frame = WithCells(20, Background + 50);

        policy.Observe(frame, Second).ShouldBeFalse();
        policy.Observe(frame, Second + ScreenChangePolicy.Settle).ShouldBeFalse();
        policy.Observe(frame, ScreenChangePolicy.MinInterval - TimeSpan.FromSeconds(0.1)).ShouldBeFalse();
        policy.Observe(frame, ScreenChangePolicy.MinInterval).ShouldBeTrue();
    }

    [Fact]
    public void Observe_WhenChangeReturnsToRecordedScreen_DoesNotChange()
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);

        policy.Observe(WithCells(20, Background + 50), at);

        for (int second = 1; second <= 30; second++)
        {
            policy.Observe(Plain(), at + second * Second).ShouldBeFalse();
        }
    }

    [Fact]
    public void Observe_AfterChange_ComparesWithNewRecordedScreen()
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);
        ScreenGrid frame = WithCells(20, Background + 50);
        policy.Observe(frame, at);
        policy.Observe(frame, at + ScreenChangePolicy.Settle).ShouldBeTrue();

        TimeSpan back = at + ScreenChangePolicy.Settle + ScreenChangePolicy.MinInterval;
        policy.Observe(Plain(), back).ShouldBeFalse();
        policy.Observe(Plain(), back + ScreenChangePolicy.Settle).ShouldBeTrue();
    }

    [Fact]
    public void Observe_WhenTimeGoesBack_Throws()
    {
        ScreenChangePolicy policy = Watching(out TimeSpan at);
        policy.Observe(Plain(), at);

        Should.Throw<DomainException>(() => policy.Observe(Plain(), at - TimeSpan.FromMilliseconds(1)))
            .Code.ShouldBe(ScreenErrors.TimeWentBack);
    }

    [Fact]
    public void FromRgb_WhenSeveralPointsPerCell_AveragesBrightness()
    {
        const int scale = 2;
        int width = ScreenGrid.Columns * scale;
        int height = ScreenGrid.Rows * scale;
        byte[] rgb = new byte[width * height * 3];

        // Первая ячейка: две белые точки и две чёрные — средняя яркость 127.5, округляется до 128; чистый красный — 76,
        // чистый зелёный — 150, четвёртая ячейка чёрная.
        Paint(rgb, width, 0, 0, 255, 255, 255);
        Paint(rgb, width, 1, 1, 255, 255, 255);
        Paint(rgb, width, scale, 0, 255, 0, 0);
        Paint(rgb, width, scale + 1, 0, 255, 0, 0);
        Paint(rgb, width, scale, 1, 255, 0, 0);
        Paint(rgb, width, scale + 1, 1, 255, 0, 0);

        for (int point = 0; point < scale * scale; point++)
        {
            Paint(rgb, width, 2 * scale + point % scale, point / scale, 0, 255, 0);
        }

        ScreenGrid grid = ScreenGrid.FromRgb(width, height, rgb);

        grid[0].ShouldBe((byte)128);
        grid[1].ShouldBe((byte)76);
        grid[2].ShouldBe((byte)150);
        grid[3].ShouldBe((byte)0);
    }

    [Fact]
    public void FromRgb_WhenSizeNotMultipleOfGrid_CoversEveryCell()
    {
        const int width = ScreenGrid.Columns + 37;
        const int height = ScreenGrid.Rows + 11;
        byte[] rgb = Enumerable.Repeat((byte)200, width * height * 3).ToArray();

        ScreenGrid grid = ScreenGrid.FromRgb(width, height, rgb);

        Enumerable.Range(0, ScreenGrid.Columns * ScreenGrid.Rows).Select(cell => grid[cell]).ShouldAllBe(value => value == 200);
    }

    [Theory]
    [InlineData(ScreenGrid.Columns - 1, ScreenGrid.Rows)]
    [InlineData(ScreenGrid.Columns, ScreenGrid.Rows - 1)]
    public void FromRgb_WhenSmallerThanGrid_Throws(int width, int height)
    {
        Should.Throw<DomainException>(() => ScreenGrid.FromRgb(width, height, new byte[width * height * 3]))
            .Code.ShouldBe(ScreenErrors.InvalidFrame);
    }

    [Fact]
    public void FromRgb_WhenBufferSizeWrong_Throws()
    {
        Should.Throw<DomainException>(() => ScreenGrid.FromRgb(ScreenGrid.Columns, ScreenGrid.Rows, new byte[10]))
            .Code.ShouldBe(ScreenErrors.InvalidFrame);
    }

    /// <summary>
    /// Экран записан и неподвижен дольше промежутка между сменами: следующее изменение ждёт только «устоялась».
    /// </summary>
    private static ScreenChangePolicy Watching(out TimeSpan at)
    {
        ScreenChangePolicy policy = new();
        policy.Observe(Plain(), TimeSpan.Zero);
        at = ScreenChangePolicy.MinInterval + Second;
        policy.Observe(Plain(), at).ShouldBeFalse();
        at += Second;
        return policy;
    }

    private static ScreenGrid Plain() => WithCells(0, Background);

    /// <summary>
    /// Первые <paramref name="count"/> ячеек — яркости <paramref name="value"/>, остальные — фон.
    /// </summary>
    private static ScreenGrid WithCells(int count, int value) =>
        Grid(cell => cell < count ? value : Background);

    /// <summary>
    /// Нижняя треть экрана мигает каждый снимок (видео); последние <paramref name="settled"/> ячеек — неподвижное
    /// изменение.
    /// </summary>
    private static ScreenGrid Video(int frame, int settled = 0)
    {
        const int cells = ScreenGrid.Columns * ScreenGrid.Rows;

        return Grid(cell => cell >= cells - settled ? Background + 50
            : cell >= cells * 2 / 3 ? (frame % 2 == 0 ? 20 : 220)
            : Background);
    }

    private static ScreenGrid Grid(Func<int, int> brightness)
    {
        byte[] rgb = new byte[ScreenGrid.Columns * ScreenGrid.Rows * 3];

        for (int cell = 0; cell < ScreenGrid.Columns * ScreenGrid.Rows; cell++)
        {
            byte value = (byte)brightness(cell);
            rgb[cell * 3] = value;
            rgb[cell * 3 + 1] = value;
            rgb[cell * 3 + 2] = value;
        }

        return ScreenGrid.FromRgb(ScreenGrid.Columns, ScreenGrid.Rows, rgb);
    }

    private static void Paint(byte[] rgb, int width, int x, int y, byte red, byte green, byte blue)
    {
        int offset = (y * width + x) * 3;
        rgb[offset] = red;
        rgb[offset + 1] = green;
        rgb[offset + 2] = blue;
    }
}
