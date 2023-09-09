using SkiaSharp;

public static class ISKBitmapExtensions
{
    public static SKBitmap Crop(this SKBitmap bmp, int width, int height)
    {
        var cropped = new SKBitmap(width, height);
        
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(bmp, (width - bmp.Width) / 2, (height - bmp.Height) / 2);
        }

        return cropped;
    }

    public static SKBitmap Save(this SKBitmap bmp, string filename)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 80);
        
        using (var stream = File.OpenWrite(filename))
        {
            data.SaveTo(stream);   
        }

        return bmp;
    }

    public static SKBitmap Resize(this SKBitmap bmp, int width, int height)
    {
        var resized = new SKBitmap(width, height);

        using (var c = new SKCanvas(resized))
        {
            c.DrawBitmap(bmp, new SKRect(0, 0, width, height), new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High });
        }     

        return resized;   
    }
}