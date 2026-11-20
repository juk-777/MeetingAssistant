using System.Diagnostics;
using System.Globalization;
using MeetingAssistant.Application.Meetings.Screen;
using MeetingAssistant.Domain.Screens;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.Infrastructure.Screen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Стенд правила смены экрана (docs/specs/2026-11-20-screen-capture.md, критерий 4): тот же захват, что у
// meeting start --screen, и то же доменное правило, а не его копия.
//   capture <файл сеток> <секунды> [монитор] — снимок раз в секунду, в файл — время и сетка яркости каждого кадра
//                                              (сам снимок не хранится);
//   check <файл сеток>                        — правило по серии: время каждой смены.
const int CellCount = ScreenGrid.Columns * ScreenGrid.Rows;

return args switch
{
    ["capture", var file, var seconds, .. var rest] => Capture(file, int.Parse(seconds, CultureInfo.InvariantCulture),
        rest.Length > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : null),
    ["check", var file] => Check(file),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("screen-bench capture <файл сеток> <секунды> [монитор] | check <файл сеток>");
    return 2;
}

static int Capture(string file, int seconds, int? monitor)
{
    IConfiguration configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:MeetingsRoot"] = Path.GetTempPath() })
        .Build();
    using ServiceProvider provider = new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
    var opened = provider.GetRequiredService<IScreenCapture>().Open(monitor);

    if (opened.IsFailure)
    {
        Console.Error.WriteLine($"{opened.Error.Code}: {opened.Error.Description}");
        return 1;
    }

    using var output = new BinaryWriter(File.Create(file));
    var clock = Stopwatch.StartNew();

    for (int frame = 0; frame < seconds; frame++)
    {
        TimeSpan due = TimeSpan.FromSeconds(frame);
        TimeSpan wait = due - clock.Elapsed;

        if (wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }

        double at = clock.Elapsed.TotalSeconds;
        ScreenFrame captured = opened.Value.Capture();
        ScreenGrid grid = ScreenGrid.FromRgb(captured.Width, captured.Height, captured.Rgb.Span);
        output.Write(at);

        for (int cell = 0; cell < CellCount; cell++)
        {
            output.Write(grid[cell]);
        }

        if (frame % 10 == 0)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{at,6:F1} с: снимок {captured.Width}×{captured.Height}, {clock.Elapsed.TotalSeconds - at:F3} с на снимок и сетку"));
        }
    }

    return 0;
}

static int Check(string file)
{
    using var input = new BinaryReader(File.OpenRead(file));
    var policy = new ScreenChangePolicy();
    byte[] rgb = new byte[CellCount * 3];
    int frames = 0, changes = 0;

    while (input.BaseStream.Position < input.BaseStream.Length)
    {
        double at = input.ReadDouble();

        for (int cell = 0; cell < CellCount; cell++)
        {
            rgb.AsSpan(cell * 3, 3).Fill(input.ReadByte());
        }

        frames++;

        if (policy.Observe(ScreenGrid.FromRgb(ScreenGrid.Columns, ScreenGrid.Rows, rgb), TimeSpan.FromSeconds(at)))
        {
            changes++;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{at,6:F1} с: смена"));
        }
    }

    Console.WriteLine($"кадров {frames}, смен {changes}");
    return 0;
}
