using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Screens;

/// <summary>
/// Снимок экрана, сведённый к сетке ячеек: значение ячейки — средняя яркость её точек, 0–255.
/// </summary>
/// <remarks>
/// Смену экрана решает сетка, а не точки: мелкий шум сжатия видео и сглаживания шрифта тонет в среднем по ячейке,
/// а строка кода меняет несколько ячеек.
/// </remarks>
public sealed class ScreenGrid
{
    public const int Columns = 64;
    public const int Rows = 36;

    private readonly byte[] _cells;

    private ScreenGrid(byte[] cells)
    {
        _cells = cells;
    }

    /// <summary>
    /// Яркость ячейки: <paramref name="index"/> — строка × <see cref="Columns"/> + столбец.
    /// </summary>
    public byte this[int index] => _cells[index];

    /// <param name="width">Ширина снимка в точках, не меньше <see cref="Columns"/>.</param>
    /// <param name="height">Высота снимка в точках, не меньше <see cref="Rows"/>.</param>
    /// <param name="rgb">Точки построчно сверху вниз, по три байта: красный, зелёный, синий.</param>
    public static ScreenGrid FromRgb(int width, int height, ReadOnlySpan<byte> rgb)
    {
        if (width < Columns || height < Rows || rgb.Length != (long)width * height * 3)
        {
            throw new DomainException(
                ScreenErrors.InvalidFrame,
                $"Снимок экрана — не меньше {Columns}×{Rows} точек по три байта: {width}×{height}, {rgb.Length} байт.");
        }

        long[] sums = new long[Columns * Rows];
        long[] counts = new long[Columns * Rows];

        for (int y = 0; y < height; y++)
        {
            int row = (int)((long)y * Rows / height);
            ReadOnlySpan<byte> line = rgb.Slice(y * width * 3, width * 3);

            for (int x = 0; x < width; x++)
            {
                int cell = row * Columns + (int)((long)x * Columns / width);
                ReadOnlySpan<byte> point = line.Slice(x * 3, 3);
                sums[cell] += 299 * point[0] + 587 * point[1] + 114 * point[2];
                counts[cell]++;
            }
        }

        byte[] cells = new byte[Columns * Rows];

        for (int cell = 0; cell < cells.Length; cell++)
        {
            cells[cell] = (byte)Math.Round(sums[cell] / (1000.0 * counts[cell]), MidpointRounding.AwayFromZero);
        }

        return new ScreenGrid(cells);
    }
}
