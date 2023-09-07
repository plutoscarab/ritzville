using SkiaSharp;

public static class IFilenameExtensions
{
    public static void SplitLines(this string filename, char ch, Action<string[]> columnAction)
    {
        using var file = File.OpenText(filename);
        string? line;

        while ((line = file.ReadLine()) != null)
        {
            var cols = line.Split(ch, StringSplitOptions.RemoveEmptyEntries);
            columnAction(cols);
        }
    }

    public static SKBitmap AsBitmap(this string filename)
    {
        return SKBitmap.Decode(filename);
    }
}