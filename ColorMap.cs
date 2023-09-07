
using SkiaSharp;

public class ColorMap
{
    byte[] map = new byte[256];

    private ColorMap()
    {
    }

    public static ColorMap FromGamma(double gamma)
    {
        var c = new ColorMap();

        for (var i = 0; i < 256; i++)
        {
            var g = i / 255.0;
            g = Math.Pow(g, 1.0 / gamma);
            c.map[i] = (byte)Math.Round(g * 255.0);
        }

        return c;
    }

    public SKColor Map(SKColor color)
    {
        return new SKColor(map[color.Red],  map[color.Green], map[color.Blue], color.Alpha);
    }

    public void Map(SKBitmap bitmap)
    {
        var pix = bitmap.Pixels;

        for (var i = 0; i < pix.Length; i++)
        {
            pix[i] = Map(pix[i]);
        }

        bitmap.Pixels = pix;        
    }
}