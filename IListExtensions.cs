public static class IListExtensions
{
    public static void Write<T>(this IEnumerable<T> items, TextWriter writer, string delimiter)
    {
        using (var e = items.GetEnumerator())
        {
            if (e.MoveNext())
                writer.Write(e.Current);

            while (e.MoveNext())
            {
                writer.Write(delimiter);
                writer.Write(e.Current);
            }
        }
    }

    public static List<T> Scramble<T>(this List<T> items, Random rand)
    {
        var p = new int[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            var j = rand.Next(i + 1);
            p[i] = p[j];
            p[j] = i;
        }

        return p.Select(i => items[i]).ToList();
    }

    public static string AsEnglish<T>(this List<T> items)
    {
        var s = items.Select(i => i?.ToString() ?? "null").ToList();
        if (s.Count > 1) s[^1] = "and " + s[^1];
        var join = s.Count > 2 ? ", " : " ";
        return string.Join(join, s);
    }
}