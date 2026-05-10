namespace VideoEmpty.Core.Model;

public readonly record struct NormalizedPoint(double X, double Y)
{
    public static NormalizedPoint Clamp(double x, double y) =>
        new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
}

public readonly record struct Size(int Width, int Height);

public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    public static Color White => new(255, 255, 255);
    public static Color Black => new(0, 0, 0);
    public static Color Transparent => new(0, 0, 0, 0);

    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public static Color FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 6)
            return new Color(
                Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
        if (s.Length == 8)
            return new Color(
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16),
                Convert.ToByte(s.Substring(6, 2), 16),
                Convert.ToByte(s.Substring(0, 2), 16));
        throw new FormatException($"Invalid color hex '{hex}'.");
    }
}
