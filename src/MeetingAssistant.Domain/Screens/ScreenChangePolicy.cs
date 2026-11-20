using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Domain.Screens;

/// <summary>
/// Сменился ли экран встречи: изменение против последнего записанного экрана, которое перестало меняться.
/// </summary>
/// <remarks>
/// Ячейки, которые меняются на каждом снимке (анимация), не устаиваются и смену не дают. Видео собеседников в окне
/// звонка движется плавно, и его ячейки успевают постоять неподвижно — поэтому живая ячейка, которая недавно менялась
/// часто, тоже не считается. Допечатанный и остановленный код — даёт смену. Пороги — константы домена, их задаёт
/// замер на снимках экрана (docs/measurements.md, «Смена экрана»).
/// </remarks>
public sealed class ScreenChangePolicy
{
    /// <summary>
    /// Разница яркости ячейки не больше — не изменение: шум сжатия и сглаживания.
    /// </summary>
    public const int BrightnessThreshold = 4;

    /// <summary>
    /// Меньше изменённых устоявшихся ячеек — не смена: курсор, часы. Примерно строка кода.
    /// </summary>
    public const int MinChangedCells = 5;

    /// <summary>
    /// Экран снимается с этим шагом: «устоялась» — несколько снимков подряд без изменения.
    /// </summary>
    public static readonly TimeSpan CaptureInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ячейка не менялась столько — устоялась.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Смена раньше после прошлой не записывается: каждая смена — вызов модели описания.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ячейка, которая менялась столько раз за <see cref="LivelyWindow"/>, — живая (видео) и не считается.
    /// </summary>
    public const int LivelyChanges = 3;

    /// <summary>
    /// Окно, за которое считаются изменения ячейки для <see cref="LivelyChanges"/>.
    /// </summary>
    public static readonly TimeSpan LivelyWindow = TimeSpan.FromSeconds(30);

    private const int Cells = ScreenGrid.Columns * ScreenGrid.Rows;

    private readonly TimeSpan[] _changedAt = new TimeSpan[Cells];

    // Времена последних LivelyChanges изменений ячейки по кругу; следующий слот — самое старое из них.
    private readonly TimeSpan[] _recentChanges = new TimeSpan[Cells * LivelyChanges];
    private readonly int[] _changeCount = new int[Cells];
    private ScreenGrid? _recorded;
    private ScreenGrid? _previous;
    private TimeSpan _recordedAt;
    private TimeSpan _previousAt;

    /// <summary>
    /// Учитывает снимок и отвечает, сменился ли экран; при смене снимок становится записанным экраном.
    /// </summary>
    /// <param name="grid">Снимок.</param>
    /// <param name="at">Время снимка от начала наблюдения, не раньше прошлого.</param>
    public bool Observe(ScreenGrid grid, TimeSpan at)
    {
        if (_previous is null || _recorded is null)
        {
            Array.Fill(_changedAt, at);
            _previous = grid;
            _previousAt = at;
            Record(grid, at);
            return true;
        }

        if (at < _previousAt)
        {
            throw new DomainException(
                ScreenErrors.TimeWentBack,
                $"Снимок экрана раньше прошлого: {at} после {_previousAt}.");
        }

        int changed = 0;

        for (int cell = 0; cell < _changedAt.Length; cell++)
        {
            if (Differs(grid[cell], _previous[cell]))
            {
                _changedAt[cell] = at;
                _recentChanges[cell * LivelyChanges + _changeCount[cell] % LivelyChanges] = at;
                _changeCount[cell]++;
            }

            if (Differs(grid[cell], _recorded[cell]) && at - _changedAt[cell] >= Settle && !IsLively(cell, at))
            {
                changed++;
            }
        }

        _previous = grid;
        _previousAt = at;

        if (changed < MinChangedCells || at - _recordedAt < MinInterval)
        {
            return false;
        }

        Record(grid, at);
        return true;
    }

    private static bool Differs(byte left, byte right) => Math.Abs(left - right) > BrightnessThreshold;

    private bool IsLively(int cell, TimeSpan at) =>
        _changeCount[cell] >= LivelyChanges
        && at - _recentChanges[cell * LivelyChanges + _changeCount[cell] % LivelyChanges] < LivelyWindow;

    private void Record(ScreenGrid grid, TimeSpan at)
    {
        _recorded = grid;
        _recordedAt = at;
    }
}
