using MeetingAssistant.Application.Meetings.Screen;

namespace MeetingAssistant.Infrastructure.Screen;

/// <summary>
/// Уменьшение снимка экрана до размера, который модель описания принимает без своего уменьшения.
/// </summary>
internal static class ScreenImage
{
    /// <summary>
    /// Длинная сторона больше — модель уменьшает картинку сама, и снимок крупнее только тратит диск.
    /// </summary>
    public const int MaxSide = 1568;

    /// <summary>
    /// Снимок с длинной стороной не больше <paramref name="maxSide"/> и теми же пропорциями; точка — среднее точек
    /// исходника, которые она накрывает.
    /// </summary>
    public static ScreenFrame Downscale(ScreenFrame frame, int maxSide = MaxSide)
    {
        int longSide = Math.Max(frame.Width, frame.Height);

        if (longSide <= maxSide)
        {
            return frame;
        }

        int width = Math.Max(1, (int)Math.Round((double)frame.Width * maxSide / longSide, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round((double)frame.Height * maxSide / longSide, MidpointRounding.AwayFromZero));
        byte[] result = new byte[width * height * 3];
        ReadOnlySpan<byte> source = frame.Rgb.Span;

        for (int y = 0; y < height; y++)
        {
            int top = (int)((long)y * frame.Height / height);
            int bottom = Math.Max(top + 1, (int)((long)(y + 1) * frame.Height / height));

            for (int x = 0; x < width; x++)
            {
                int left = (int)((long)x * frame.Width / width);
                int right = Math.Max(left + 1, (int)((long)(x + 1) * frame.Width / width));
                int red = 0, green = 0, blue = 0;

                for (int sourceY = top; sourceY < bottom; sourceY++)
                {
                    ReadOnlySpan<byte> line = source.Slice((sourceY * frame.Width + left) * 3, (right - left) * 3);

                    for (int point = 0; point < line.Length; point += 3)
                    {
                        red += line[point];
                        green += line[point + 1];
                        blue += line[point + 2];
                    }
                }

                int count = (bottom - top) * (right - left);
                int offset = (y * width + x) * 3;
                result[offset] = (byte)((red + count / 2) / count);
                result[offset + 1] = (byte)((green + count / 2) / count);
                result[offset + 2] = (byte)((blue + count / 2) / count);
            }
        }

        return new ScreenFrame(width, height, result);
    }
}
