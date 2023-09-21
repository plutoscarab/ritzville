#define COST_SHIFT
#define GAME_CRAFTER

using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text;
using SkiaSharp;

record CardInfo(int Id, int Index, int[] Cost, int Points, int Bonus);

record Card(int Id, int[] Cost, int Points, int Bonus, int Color, int CouponValue, string Name)
{
    public float Angle = ((Id * ((float)Math.Sqrt(5) - 1) / 2) % 1f) * 4 - 2;

    public DateTime Acquision { get; private set; } = DateTime.UtcNow;

    public Card Stamped()
    {
        return new Card(Id, Cost, Points, Bonus, Color, CouponValue, Name);
    }

    public int[] EffectiveCost(List<Card> playerCards) =>
        Cost.Select((c, i) => Math.Max(0, c - playerCards.Sum(card => card.Color == i ? card.CouponValue : 0))).ToArray();

    public int[] NetCost(List<Card> playerCards, int[] playerChips) =>
        Cost.Select((c, i) => Math.Max(0, c - playerCards.Sum(card => card.Color == i ? card.CouponValue : 0) - playerChips[i])).ToArray();
}

class Program
{
    const int colors = 6;
    const float dpi = 300f;
    const float pixelsPerMillimeter = dpi / 25.4f;
    const float pixelsPerPoint = dpi / 72f;
    const int cutWidth = (int)(63 * pixelsPerMillimeter + .5);
    const int cutHeight = (int)(88 * pixelsPerMillimeter + .5);
    const int portraitSize = 50;

    static readonly SKPaint smooth = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };


    static void Main()
    {
        var p = new Program();
        p.Run();
    }

    static void OnAllCores(Action<int> action)
    {
        Parallel.ForEach(Environment.ProcessorCount.Enumerate(), action);
    }

    static void WriteDeckSummary(List<Card> deck)
    {
        using var writer = File.CreateText("gen/deck.txt");
        writer.Write("Id\tColor\tPoints\tBonus\tCoupons");

        for (var c = 0; c < colors; c++)
            writer.Write($"\tCost{c}");

        writer.WriteLine("\tName");

        foreach (var card in deck)
        {
            writer.Write($"{card.Id}\t{card.Color}\t{card.Points}\t{card.Bonus}\t{card.CouponValue}");

            foreach (var c in card.Cost)
                writer.Write($"\t{c}");

            writer.WriteLine($"\t{card.Name}");
        }
    }

    static SKColor Lerp(SKColor a, SKColor b, float t) =>
        new(
            (byte)(a.Red * (1 - t) + b.Red * t),
            (byte)(a.Green * (1 - t) + b.Green * t),
            (byte)(a.Blue * (1 - t) + b.Blue * t));

    const int maxColorsPerCard = 4;

#if GAME_CRAFTER
    const int cardWidth = 825;
    const int cardHeight = 1125;
    const int bleedX = (cardWidth - cutWidth) / 2;
    const int bleedY = (cardHeight - cutHeight) / 2;
#else
    const int bleedX = 36;
    const int bleedY = 36;
    const int cardWidth = cutWidth + bleedX * 2;
    const int cardHeight = cutHeight + bleedY * 2;
#endif

    const int safeArea = 36;
    const int centerX = cardWidth / 2;
    const int centerY = cardHeight / 2;
    const double bigDotRadius = 3.5 * pixelsPerMillimeter;
    const double dotPenSize = .25 * pixelsPerMillimeter;

    readonly List<string> sectors = new();
    readonly List<SKColor> colorScheme = new();
    readonly List<SKPaint> cardPaint = new();
    readonly Dictionary<string, SKBitmap> discImage = new();
    readonly Dictionary<string, SKBitmap> insetImage = new();
    readonly ColorMap gammaMap = ColorMap.FromGamma(1.0);

    static SKPaint GetFont(string family, SKFontStyle style, SKTextAlign align, float points) =>
        new() { Color = SKColors.Black, IsAntialias = true, Typeface = SKTypeface.FromFamilyName(family, style), TextAlign = align, TextSize = points * (float)pixelsPerPoint };

    readonly SKPaint pointsPaint = GetFont("Stencil", SKFontStyle.Bold, SKTextAlign.Right, 24f);
    readonly SKPaint bonusPaint = GetFont("Stencil", SKFontStyle.Bold, SKTextAlign.Right, 12f);
    readonly SKPaint namePaint = GetFont("Candara", SKFontStyle.Bold, SKTextAlign.Center, 12f);
    readonly SKPaint idPaint = GetFont("Candara", SKFontStyle.Normal, SKTextAlign.Left, 3f);

    void WriteSectorImages()
    {
        var sectorNum = 0;

        foreach (var sector in sectors)
        {
            // Trim the card-front background art to the correct aspect ratio.
            using (var bmp = $"art/{sector}.jpg".AsBitmap())
            {
                insetImage[sector] = bmp.Crop((bmp.Width * cardWidth) / cardHeight, bmp.Height).Save($"gen/{sector}-inset.png");
            }

            using (var bmp = $"art/{sector}-symbol.jpg".AsBitmap())
            {
                var symbol = new SKBitmap(bmp.Width, bmp.Height);

                // Convert the chip art into white-on-transparent.
                var schemeColor = colorScheme[sectorNum];
                var pixels = bmp.Pixels;
                var symPixels = new SKColor[pixels.Length];

                for (var i = 0; i < pixels.Length; i++)
                {
                    var pix = pixels[i];
                    pix.ToHsv(out _, out _, out var br);
                    var alpha = 1f - (br / 100f);
                    symPixels[i] = SKColors.White.WithAlpha((byte)(255 * alpha));
                    pixels[i] = Lerp(schemeColor, SKColors.White, alpha);
                }

                bmp.Pixels = pixels;
                symbol.Pixels = symPixels;
                bmp.Save($"gen/{sector}-chip.png");

                // Create the disc graphic from the symbol.
                const int thickness = 40;
                using (var canvas = new SKCanvas(symbol))
                using (var brush = new SKPaint { Color = schemeColor })
                using (var pen = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = thickness, IsAntialias = true })
                {
                    canvas.DrawCircle(symbol.Width / 2, symbol.Height / 2, symbol.Width / 2 - thickness / 2, brush);
                    canvas.DrawCircle(symbol.Width / 2, symbol.Height / 2, symbol.Width / 2 - thickness / 2, pen);

                    using var path = new SKPath();
                    path.AddCircle(symbol.Width / 2, symbol.Height / 2, symbol.Width / 2 - thickness - 1);
                    path.Close();
                    canvas.ClipPath(path);
                    var w = (symbol.Width * 3) / 4;
                    var h = (symbol.Height * 3) / 4;
                    canvas.DrawBitmap(bmp, new SKRect((symbol.Width - w) / 2, (symbol.Height - h) / 2, (symbol.Width + w) / 2, (symbol.Height + h) / 2), smooth);
                }

                discImage[sector] = symbol.Save($"gen/{sector}-disc.png");
                symbol = new SKBitmap(bmp.Width, bmp.Height);

                using (var canvas = new SKCanvas(symbol))
                using (var brush = new SKPaint { Color = schemeColor })
                using (var pen = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = thickness, IsAntialias = true })
                {
                    canvas.Clear(brush.Color);
                    var w = (symbol.Width * 3) / 4;
                    var h = (symbol.Height * 3) / 4;
                    canvas.DrawBitmap(bmp, new SKRect((symbol.Width - w) / 2, (symbol.Height - h) / 2, (symbol.Width + w) / 2, (symbol.Height + h) / 2), smooth);
                }

#if GAME_CRAFTER
                symbol = symbol.Resize(300, 300);
#endif
                symbol.Save($"gen/{sector}-token[all,8].png");
            }

            sectorNum++;
        }
    }

    void WriteBonusImages()
    {
#if GAME_CRAFTER
        const int r = 150;
        const float scale = r * 2 / 1.75f / dpi;
#else
        const int r = (int)(dpi * 1.75 / 2);
        const float scale = 1f;
#endif
        const int safe = (int)(dpi * .25 * scale);

        for (var b = 2; b < 4; b++)
        {
            using var bmp = new SKBitmap(r * 2, r * 2);

            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(new SKColor(109, 179, 205));
                // using var brush = new SKPaint { Color = new SKColor(109, 179, 205) };
                // canvas.DrawCircle(r, r, r - safe / 2, brush);

                // using var pen = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = safe, IsAntialias = true };
                // canvas.DrawCircle(r, r, r - safe / 2, pen);

                using (var font = GetFont("Candal", SKFontStyle.Normal, SKTextAlign.Center, 44f * scale))
                {
                    font.Color = SKColors.White;
                    var fontHeight = (font.FontMetrics.Descent - font.FontMetrics.Ascent) / 2 - font.FontMetrics.Descent;
                    canvas.DrawText($"+{b}", r,  r + fontHeight - 15, font);
                }

                using (var font = GetFont("Adamina", SKFontStyle.Bold, SKTextAlign.Center, 10f * scale))
                {
                    font.Color = SKColors.White;
                    var fontHeight = (font.FontMetrics.Descent - font.FontMetrics.Ascent) / 2 - font.FontMetrics.Descent;

                    using (var path = new SKPath())
                    {
                        path.AddCircle(r, r, r - safe - fontHeight * 1.1f);
                        path.Close();
                        canvas.Translate(r, r);
                        canvas.RotateDegrees(90);
                        canvas.Translate(-r, -r);
                        canvas.DrawTextOnPath($"BONUS POINTS", path, new SKPoint(0, fontHeight), font);
                        canvas.ResetMatrix();
                    }

                    using (var path = new SKPath())
                    {
                        path.AddCircle(r, r, r - safe - fontHeight * 1.1f, SKPathDirection.CounterClockwise);
                        path.Close();
                        canvas.Translate(r, r);
                        canvas.RotateDegrees(-90);
                        canvas.Translate(-r, -r);
                        canvas.DrawTextOnPath($"FOR NO-CHIP BUY", path, new SKPoint(0, fontHeight), font);
                        canvas.ResetMatrix();
                    }
                }
            }

            bmp.Save($"gen/bonus{b}[all,8].png");
        }
    }

    void DrawCard(Card card, SKBitmap skbmp)
    {
        var sector = sectors[card.Color];
        var color = colorScheme[card.Color];

        using (var canvas = new SKCanvas(skbmp))
        {
            // Card background image.
            canvas.DrawBitmap(insetImage[sector], new SKRect(0, 0, cardWidth, cardHeight), smooth);
            canvas.Flush();

            // Apply hue.
            color.ToHsl(out var hue, out var sat, out var lvl);
            var satAdj = card.Color == 4 ? .4f : 1f;
            var lerp = card.Color == 3 ? .8f : card.Color == 5 ? .5f : .6f;
            var pixels = skbmp.Pixels;

            for (var i = 0; i < pixels.Length; i++)
            {
                var q = pixels[i];
                q.ToHsl(out _, out _, out var lvl2);
                var p = SKColor.FromHsl(hue, sat, lvl2);
                p = Lerp(p, q, lerp);
                p.ToHsl(out var hue2, out var sat2, out lvl2);
                p = SKColor.FromHsl(hue2, sat2 * satAdj, lvl2);
                pixels[i] = p;
            }

            skbmp.Pixels = pixels;

            const int marginX = bleedX + safeArea;
            const int marginY = bleedY + safeArea;
            const double couponX = marginX + (bigDotRadius + dotPenSize / 2) * 1.5;
            const double couponY = marginY + (bigDotRadius + dotPenSize / 2) * 1.5;

            // Coupon symbols.
            for (var i = 0; i < card.CouponValue; i++)
                canvas.DrawBitmap(
                    discImage[sector],
                    SKRect.Create(
                        (int)(couponX - bigDotRadius * 1.5 + bigDotRadius * 3.5 * i),
                        (int)(couponY - bigDotRadius * 1.5),
                        (int)(bigDotRadius * 3),
                        (int)(bigDotRadius * 3)),
                    smooth);

            // Point value.
            float cursorY = marginY;

            if (card.Points > 0)
            {
                cursorY += pointsPaint.TextSize - 20;
                canvas.DrawText(card.Points.ToString(), cardWidth - marginX, cursorY, pointsPaint);
            }

            // Bonus point value.
            if (card.Bonus > 0)
            {
                cursorY += bonusPaint.TextSize * 1.5f;
                canvas.DrawText($"(+{card.Bonus})", cardWidth - marginX, cursorY, bonusPaint);
            }

            // Card ID.
            canvas.DrawText($"{card.Id}", marginX, centerY, idPaint);

            // Cost symbols.
            const int maxPerCol = 5;
            var cost = card.Cost;
            var dotCount = cost.Count(c => c > 0) + cost.Count(c => c > maxPerCol);
            const double dotSpacing = bigDotRadius * 2.5;
            var x0 = centerX - (dotCount - 1) * dotSpacing / 2;
            var x = x0;

            for (var i = 0; i < colors; i++)
            {
                var yc = cost[i] > maxPerCol ? (cost[i] + 1) / 2 : cost[i];
                var y = centerY - (yc - 1) * dotSpacing / 2;

                for (var j = 0; j < cost[i]; j++)
                {
                    canvas.DrawBitmap(discImage[sectors[i]], new SKRect((int)(x - bigDotRadius), (int)(y - bigDotRadius), (int)(x + bigDotRadius), (int)(y + bigDotRadius)), smooth);

                    y += dotSpacing;

                    if (cost[i] > maxPerCol && j + 1 == yc)
                    {
                        y = centerY - (cost[i] - yc - 1) * dotSpacing / 2;
                        x += dotSpacing;
                    }
                }

                if (cost[i] > 0)
                {
                    x += dotSpacing;
                }
            }

            // Business name.
            if (namePaint.MeasureText(card.Name) > cutWidth)
                Debugger.Break();

            canvas.DrawText(card.Name, centerX, cardHeight - bleedY - safeArea - 20, namePaint);

            // Gamma correction.
            gammaMap.Map(skbmp);
        }

        skbmp.Save($"cards/card{card.Id}.png");
    }

    void Run()
    {
        "sectors.txt".SplitLines(' ', cols =>
        {
            sectors.Add(cols[0]);
            var color = SKColor.Parse(cols[1]);
            colorScheme.Add(color);
            cardPaint.Add(new SKPaint { Color = color.WithAlpha(128) });
        });

        var names = sectors.ToDictionary(s => s, s => new List<string>());

        "names.txt".SplitLines('\t', tabs =>
        {
            if (tabs.Count() >= 2)
                names[tabs[0]].Add(tabs[1]);
        });

        var wordIndex = 0;
        var cardCosts = new List<int[]>();

        foreach (var lw in LyndonWords(colors, 3).Skip(1).Reverse())
        {
            var g = lw.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());

            // Must contain at least one 0.
            if (!g.ContainsKey(0))
                continue;

            // Must contain sufficient number of 0's.
            if (g[0] < colors - maxColorsPerCard)
                continue;

            // Must contain at least one 1.
            if (!g.ContainsKey(1)) continue;

            if (g.ContainsKey(2))
            {
                // Cannot contain more than one 2.
                if (g[2] > 1) continue;

                // Cannot contain more 2's than 1's.
                if (g[2] > g[1]) continue;

                // The 2 must be in the last position.
                if (lw.Last() != 2) continue;
            }

            Console.Write($"{++wordIndex,2}: ");

            // Determine cost associated with 1's.
            var baseCost = (wordIndex + 3) / 4;

            // Add 50% for the cost of 2's.
            var map = new[] { 0, baseCost, baseCost + (baseCost + 1) / 2 };

            // Replace indices with costs.
            var card = lw.Select(n => map[n]).ToList();

            card.Write(Console.Out, " ");
            Console.WriteLine();

            // Generate card of each color using the current pattern.
            for (var i = 0; i < colors; i++)
            {
#if COST_SHIFT
                var j = (i + wordIndex - 1) % colors;
                cardCosts.Add(card.Skip(colors - j).Take(j).Concat(card.Take(colors - j)).ToArray());
#else                
                cardCosts.Add(card.Skip(colors - i).Take(i).Concat(card.Take(colors - i)).ToArray());
#endif
            }
        }

        // Keep track of how early each card is purchased in simulations.
        var firstBuys = cardCosts.Count.InitArray(c => new ConcurrentBag<int>());

        // Run simulations on all processors.
        OnAllCores(pid =>
        {
            var rand = new Random(99169 + pid);

            for (var game = 0; game < 10_000 / Environment.ProcessorCount; game++)
            {
                var deal = new List<int[]>(cardCosts);
                var tableau = new List<int[]>();

                for (var i = 0; i < 15; i++)
                    tableau.Add(Draw(deal, rand));

                const int players = 2;
                var turn = 0;
                var chips = players.InitArray(() => new int[colors]);
                var coupons = players.InitArray(() => new int[colors]);
                var score = new int[players];

                while (deal.Any() || tableau.Any())
                {
                    turn++;

                    for (var p = 0; p < players; p++)
                    {
                        var affordable = tableau.Count.Enumerate().Where(c => colors.Enumerate().All(i => tableau[c][i] <= chips[p][i] + coupons[p][i])).ToList();
                        var maxColors = colors.Enumerate().Select(c => tableau.Max(t => t[c])).ToList();
                        var colorsDisallowed = colors.Enumerate().Where(c => maxColors[c] <= chips[p][c] + coupons[p][c]).ToList();

                        if ((rand.Next(2) == 0 && colorsDisallowed.Count <= 3) || !affordable.Any())
                        {
                            // Take chips
                            var choices = colors.Enumerate().Where(c => chips[p][c] > 0 && rand.Next(2) == 0).ToList();
                            var others = colors.Enumerate().Except(choices).ToList();
                            choices = choices.Scramble(rand);
                            choices.AddRange(others.Scramble(rand));
                            choices = choices.Except(colorsDisallowed).ToList().Scramble(rand).Concat(colorsDisallowed.Scramble(rand)).ToList();

                            for (var k = 0; k < 3; k++)
                                ++chips[p][choices[k]];
                        }
                        else
                        {
                            // Choose card to buy
                            var purchase = affordable[rand.Next(affordable.Count)];
                            var tc = tableau[purchase];

                            // Replace it in tableau
                            tableau.RemoveAt(purchase);
                            if (deal.Any()) tableau.Add(Draw(deal, rand));

                            // Pay chips
                            for (var i = 0; i < colors; i++)
                                chips[p][i] -= Math.Max(0, tc[i] - coupons[p][i]);

                            // Add coupon
                            var cardIndex = cardCosts.FindIndex(c => Enumerable.SequenceEqual(c, tc));
                            coupons[p][cardIndex % colors]++;

                            // Track when card was acquired
                            firstBuys[cardIndex].Add(turn);

                            if (!deal.Any() && !tableau.Any())
                                break;
                        }
                    }
                }
            }
        });

        var bySpeed = new List<(int, double)>();

        for (var i = 0; i < cardCosts.Count / colors; i++)
        {
            var mins = Enumerable.Range(i * colors, colors).Select(j => firstBuys[j].Min()).ToList();
            var avg = mins.Average();
            bySpeed.Add((i, avg));
        }

        var deckInfo = new List<CardInfo>();
        var cardId = 0;
        var deck = new List<Card>();

        foreach (var (i, avg) in bySpeed.OrderBy(_ => _.Item2).ThenBy(_ => cardCosts[_.Item1 * colors].Sum()))
        {
            var card = cardCosts[i * colors];
            var points = Math.Min(5, (int)Math.Round(avg) - 3);
            var bonus = points > 0 && card.Where(c => c > 0).Min() >= 3 && card.Sum() >= 6 ? (card.Max() + 1) / 2 : 0;

            if (points == 5 && bonus == 4)
                continue;   // skip these 6 cards to save one sheet in printing

            Console.Write($"{i + 1,2}: {avg:F2}\t");
            card.Write(Console.Out, " ");
            Console.Write($"\t{points}");
            var coupon = 1;

            if (points == 4 && card.Count(c => c > 0) >= 3)
            {
                coupon = 2;
                bonus = 0;
                Console.Write(" x 2");
            }

            if (bonus > 0)
                Console.Write($" (+{bonus})");

            Console.WriteLine();
            deckInfo.Add(new CardInfo(cardId, i, card.ToArray(), points, bonus));

            for (var c = 0; c < colors; c++)
            {
                var index = i * colors + c;
                deck.Add(new Card(deck.Count, cardCosts[index], points, bonus, c, coupon, names[sectors[c]][cardId]));
            }

            cardId++;
        }

        Directory.CreateDirectory("gen");
        WriteDeckSummary(deck);
        WriteSectorImages();
        WriteBonusImages();

        // Resize portraits.
        for (var c = 0; c < 2; c++)
        {
            $"art/character{c}.png".AsBitmap().Resize(portraitSize, portraitSize).Save($"gen/character{c}.png");
        }

        Directory.CreateDirectory("cards");
        Directory.CreateDirectory("thumb");
        Directory.CreateDirectory("cut");

        // Generate each card-front image.
        Parallel.ForEach(deck, card =>
        {
            using var skbmp = new SKBitmap(cardWidth, cardHeight);
            DrawCard(card, skbmp);

            // Create thumbnail image with trimmed edges and rounded corners.

            // Trim.
            using var rounded = skbmp.Crop(cutWidth, cutHeight);

            // Round.
            var cornerR = safeArea;
            var bx0 = safeArea - 1;
            var bx1 = cutWidth - safeArea;
            var by0 = safeArea - 1;
            var by1 = cutHeight - safeArea;

            for (var bx = 0; bx < cornerR; bx++)
            {
                for (var by = 0; by < cornerR; by++)
                {
                    var r = Math.Sqrt(bx * bx + by * by);
                    var alpha = (byte)(255 * Math.Clamp(cornerR - r, 0, 1));

                    void UpdateAlpha(int x, int y)
                    {
                        var pixel = rounded.GetPixel(x, y);
                        pixel = pixel.WithAlpha((byte)alpha);
                        rounded.SetPixel(x, y, pixel);
                    }

                    if (alpha < 255)
                    {
                        UpdateAlpha(bx0 - bx, by0 - by);
                        UpdateAlpha(bx0 - bx, by1 + by);
                        UpdateAlpha(bx1 + bx, by0 - by);
                        UpdateAlpha(bx1 + bx, by1 + by);
                    }
                }
            }

            rounded.Save($"cut/card{card.Id}.png");

            // Shrink.
            using var thumb = rounded.Resize(rounded.Width / 3, rounded.Height / 3);
            thumb.Save($"thumb/card{card.Id}.png");
        });

        var rand = new Random(99169 * 5);
        var draw = new List<Card>(deck);
        DumpTableau(NewDeal(draw, rand, 3, 5), rand, 3, "gen/tableau.png");
        ExampleGame(deck, sectors, true);
        ExampleGame(deck, sectors, false);
        //OnAllCores(_ => ExampleGame(deck, sectors, false));
    }

    Card?[] NewDeal(List<Card> draw, Random rand, int rows, int perRow)
    {
        var cards = new Card[rows * perRow];

        for (var i = 0; i < (rows - 1) * perRow; i++)
        {
            cards[i] = Draw(draw, rand);
        }

        for (var i = (rows - 1) * perRow; i < rows * perRow; i++)
        {
            while (true)
            {
                var card = Draw(draw, rand);

                if (card.Points == 0)
                {
                    cards[i] = card;
                    break;
                }

                draw.Add(card);
            }
        }

        return cards;
    }

    void DumpTableau(Card?[] tableau, Random rand, int cardRows, string filename)
    {
        var thScale = 0.25;
        var gridY = cardRows;
        var gridX = (tableau.Length + gridY - 1) / gridY;
        var gridMargin = (float)(cutWidth * thScale * 0.2);
        var thWidth = (float)(cutWidth * thScale);
        var thHeight = (float)(cutHeight * thScale);

        using var bmp = new SKBitmap((int)Math.Ceiling((thWidth + gridMargin) * gridX), (int)Math.Ceiling((thHeight + gridMargin) * gridY));
        var canvas = new SKCanvas(bmp);
        var dotted = new SKPaint { Color = SKColors.DarkGray, FilterQuality = SKFilterQuality.High, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 12f, 12f }, 0f), StrokeWidth = 6 };

        for (var y = 0; y < gridY; y++)
        {
            for (var x = 0; x < gridX; x++)
            {
                var px = x * (thWidth + gridMargin) + gridMargin / 2;
                var py = y * (thHeight + gridMargin) + gridMargin / 2;
                var angle = (float)rand.NextDouble() * 4 - 2;
                var card = tableau[y * gridX + x];

                if (card != null)
                {
                    using var cardBitmap = SKBitmap.Decode($"thumb/card{card.Id}.png");
                    canvas.Translate(px + thWidth / 2, py + thHeight / 2);
                    canvas.RotateDegrees(angle);
                    canvas.Translate(-px - thWidth / 2, -py - thHeight / 2);
                    canvas.DrawBitmap(cardBitmap, SKRect.Create(px, py, thWidth, thHeight), smooth);
                    canvas.ResetMatrix();
                }
            }
        }

        canvas.Flush();
        bmp.Save(filename);
    }

    long totalGames;
    long voidGames;
    readonly int[][] gameLengths = 6.InitArray(() => new int[1000]);
    readonly object debugTableauLock = new();
    int mostPlus2Earned = 0;
    int mostPlus3Earned = 0;
    int mostBonusEarned = 0;

    void ExampleGame(List<Card> deck, List<string> sectors, bool writeExample)
    {
        int[] bankInit = new[] { 0, 4, 5, 6, 7, 8 };
        string[] names = new[] { "Arrow", "Branch", "Cedar", "Dart", "Echo" };
        string[] colorNames = new[] { "ivory", "red", "blue", "green", "black", "purple" };
        string[] colorSingular = new[] { "an ivory", "a red", "a blue", "a green", "a black", "a purple" };
        string[] ordinalName = new[] { "no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
        var game = 0;
        var rand = new Random();
        var writer = TextWriter.Null;

        while (game < int.MaxValue)
        {
            var tg = Interlocked.Increment(ref totalGames);

            if (tg % 10_000 == 0)
            {
                Console.WriteLine($"{tg:N0}\t{voidGames:N0}");
                for (var p = 1; p < 6; p++)
                {
                    Console.Write("\t");

                    for (var i = 25; i < 50; i++)
                        Console.Write($"{gameLengths[p][i]} ");

                    Console.WriteLine();
                }
            }

            var players = writeExample ? 2 : rand.Next(1, 6);
            writer.WriteLine($"\nGame {++game:N0}");
            var wildcardHappened = false;
            var returnHappened = false;
            var extraChipsHappened = false;
            var tieHappened = false;
            var doubleCouponHappened = false;
            var bank = colors.InitArray(() => bankInit[players]);
            var chips = players.InitArray(() => new int[colors]);
            var cards = players.InitArray(() => new List<Card>());
            var score = new int[players];
            var target = new Card?[players];

            var draw = new List<Card>(deck.Where(c => players < 5 || c.Points < 5));
            var cardsPerRow = 5; //players < 5 ? 5 : 7;
            var cardRows = 3;
            Card?[] tableau = NewDeal(draw, rand, cardRows, cardsPerRow);

            const int chipsPerTurn = 3;
            const int winningScore = 25;
            const int chipLimit = 10;

            var turn = 1;
            var player = 0;
            var buyTurn = 0;
            var twosEarned = 0;
            var threesEarned = 0;
            var bonusEarned = 0;

            using (var html = new IndentedTextWriter(writeExample ? File.CreateText("gen/example.html") : TextWriter.Null))
            {
                var th = 150;
                var tw = (cutWidth * th) / cutHeight;
                var ppm = (pixelsPerMillimeter * th) / cutHeight;
                var csize = (int)(39 * ppm);
                html.WriteLine("<!DOCTYPE html>");
                html.WriteLine("<html>");
                html.Indent++;
                html.WriteLine("<head>");
                html.Indent++;
                html.WriteLine("<style>");
                html.Indent++;
                html.WriteLine($@"@import url('https://fonts.googleapis.com/css2?family=Josefin+Sans&family=Saira+Stencil+One&family=Tilt+Warp&display=swap');
            body {{ 
                margin: 2em; 
                }}
            .cards {{ 
                height: {th * 3.4}px; 
                width: {5 * tw * 1.1}px; 
                }}
            .cards img {{ 
                width: {tw}px; 
                height: {th}px; 
                position: absolute; 
                filter: drop-shadow(3px 3px 3px #888888); 
                }}
            .chips {{ 
                height: {csize * 1.3}px; 
                width: {csize * 1.3}px; 
                }}
            .chips img {{ 
                width: {csize}px; 
                height: {csize}px; 
                position: absolute; 
                }}
            .players {{ 
                padding-left: 1em; 
                }}
            .playerHeader {{ 
                display: flex; 
                justify-content: space-between; 
                }}
            .name {{ 
                font-family: 'Tilt Warp', cursive; 
                font-size: 16pt; 
                }}
            .portrait {{ 
                width: {portraitSize}px; 
                height: {portraitSize}px; 
                position: relative; 
                top: 10px; 
                }}
            .player {{ 
                display: flex; 
                background-color: #9AC; 
                width: 450px; 
                height: 180px; 
                }}
            .score {{ 
                margin: 1em; 
                font-family: 'Saira Stencil One', cursive; 
                font-size: 16px; 
                width: 30px; 
                height: 30px; 
                border: 3px solid black; 
                border-radius: 20px; 
                display: flex; 
                justify-content: center; 
                align-items: center; 
                }}
            .bonus {{ 
                margin: .4em 1.35em; 
                font-family: 'Saira Stencil One', cursive; 
                font-size: 14px; 
                width: 25px; 
                height: 25px; 
                border: 3px solid black; 
                border-radius: 20px; 
                display: flex; 
                justify-content: center; 
                align-items: center; 
                background-color: #9EF; 
                }}
            .playArea {{ 
                width: 400px; 
                }}
            #message {{ 
                font-family: 'Josefin Sans', sans-serif; 
                font-size: 16pt; 
                max-width: 700px; 
                line-height: 150%; 
                }}
            button {{ 
                margin: 1.1em; 
                color: #CCC; 
                background-color: #229; 
                border-radius: 0.5em; 
                height: 2em; 
                font-family: 'Josefin Sans', sans-serif; 
                font-size: 16pt; 
                }}
            button:hover {{ 
                color: #FFF; 
                }}
            button:disabled {{ 
                color: #444; 
                background-color: #999; 
                }}
            .flex {{ 
                display: flex; 
                }}");

                html.Indent--;
                html.WriteLine("</style>");
                html.Indent--;
                html.WriteLine("</head>");
                html.WriteLine("<body>");
                html.Indent++;
                html.WriteLine("<div class='flex'>");
                html.Indent++;
                html.WriteLine("<div class='cards'>");
                html.Indent++;

                void GetTableauXY(int index, out int x, out int y)
                {
                    var gridX = index % cardsPerRow;
                    var gridY = index / cardsPerRow;
                    x = (int)(gridX * tw * 1.1 + 20);
                    y = (int)(gridY * (th + tw * .1) + 20);
                }

                for (var i = 0; i < tableau.Length; i++)
                {
                    var card = tableau[i];

                    if (card == null)
                        continue;

                    GetTableauXY(i, out var x, out var y);
                    html.WriteLine($"<img id='card{card.Id}' src='../thumb/card{card.Id}.png' style='left: {x:F0}px; top: {y:F0}px; transform: rotate({card.Angle:F2}deg);'>");
                }

                foreach (var card in draw)
                {
                    html.WriteLine($"<img id='card{card.Id}' src='../thumb/card{card.Id}.png' style='left: -200px; top: 200px; transform: rotate({card.Angle:F2}deg);'>");
                }

                html.Indent--;
                html.WriteLine("</div>");
                html.WriteLine("<div class='chips'>");
                html.Indent++;
                var chipIds = colors.InitArray(() => new Stack<string>());
                var playerChipIds = players.InitArray(() => colors.InitArray(() => new Stack<string>()));

                void GetBankXY(int color, out int x, out int y)
                {
                    x = (int)(5.2 * tw * 1.1 + rand.NextDouble() * 4 - 2);
                    y = (int)(color * csize * 1.2 - 4 * bank[color] + 25);
                }

                for (var c = 0; c < colors; c++)
                {
                    var b = bank[c];
                    bank[c] = 0;

                    for (var j = 0; j < b; j++)
                    {
                        GetBankXY(c, out var x, out var y);
                        bank[c]++;
                        var chipId = $"chip{j * colors + c}";
                        html.WriteLine($"<img id='{chipId}' src='{sectors[c]}-disc.png' style='left: {x:F0}px; top: {y:F0}px;'>");
                        chipIds[c].Push(chipId);
                    }
                }

                html.Indent--;
                html.WriteLine("</div>");
                html.WriteLine("<div class='players'>");
                html.Indent++;

                for (var p = 0; p < players; p++)
                {
                    html.WriteLine("<div class='playerHeader'>");
                    html.Indent++;
                    html.WriteLine($"<p class='name'>{names[p]}</p>");
                    html.WriteLine($"<img class='portrait' src='character{p}.png'>");
                    html.Indent--;
                    html.WriteLine("</div>");
                    html.WriteLine("<div class='player'>");
                    html.Indent++;
                    html.WriteLine("<div>");
                    html.Indent++;
                    html.WriteLine($"<div id='score{p}' class='score'>0</div>");
                    html.WriteLine($"<div id='bonus{p}' class='bonuses'></div>");
                    html.Indent--;
                    html.WriteLine("</div>");
                    html.WriteLine($"<div id='player{p}' class='playArea'></div>");
                    html.Indent--;
                    html.WriteLine("</div>");
                }

                html.Indent--;
                html.WriteLine("</div>");
                html.Indent--;
                html.WriteLine("</div>");
                html.WriteLine("<div class='flex'>");
                html.Indent++;
                html.WriteLine("<p id='message'>This simulation will show you how the game is played. Click the button to start the game, and again after each player takes their turn.</p>");
                html.WriteLine("<button id='advance' type='button' onclick='turn1A()'>Start game</button>");
                html.Indent--;
                html.WriteLine("</div>");
                html.WriteLine("<script>");
                html.Indent++;
                html.WriteLine("var message = document.getElementById('message');");
                html.WriteLine("var advance = document.getElementById('advance');");
                html.WriteLine("var thingsMoving = [];");
                html.WriteLine("var playerX = [];");
                html.WriteLine("var playerY = [];");
                html.WriteLine("function animateFrame(timestamp)");
                html.WriteLine("{");
                html.Indent++;
                html.WriteLine("for (var i = thingsMoving.length - 1; i >= 0; i--) {");
                html.Indent++;
                html.WriteLine("var c = thingsMoving[i];");
                html.WriteLine("const pixelsPerMillisecond = 1;");
                html.WriteLine("if (c.timestamp === undefined) { c.timestamp = timestamp; c.t = 0; c.speed = pixelsPerMillisecond / Math.sqrt(Math.pow(c.startX - c.endX, 2) + Math.pow(c.startY - c.endY, 2)); }");
                html.WriteLine("if (c.startScale === undefined) c.startScale = c.chip.getBoundingClientRect().width / c.chip.offsetWidth;");
                html.WriteLine("var milliseconds = timestamp - c.timestamp;");
                html.WriteLine("c.timestamp = timestamp;");
                html.WriteLine("c.t = Math.min(1, c.t + milliseconds * c.speed);");
                html.WriteLine("var s = c.t * c.t * (3 - 2 * c.t);");
                html.WriteLine("c.chip.style.left = (c.startX + s * (c.endX - c.startX)) + 'px';");
                html.WriteLine("c.chip.style.top = (c.startY + s * s * (c.endY - c.startY)) + 'px';");
                html.WriteLine("c.chip.style.transform = 'rotate(' + c.endRotation + 'deg) scale(' + (c.startScale + s * (c.endScale - c.startScale)) + ')';");
                html.WriteLine("if (c.t >= 1) thingsMoving.splice(i, 1);");
                html.Indent--;
                html.WriteLine("}");
                html.WriteLine("if (thingsMoving.length > 0) window.requestAnimationFrame(animateFrame);");
                html.Indent--;
                html.WriteLine("}");
                html.WriteLine("function animate(chipId, x, y, scale, rotation, z)");
                html.WriteLine("{");
                html.Indent++;
                html.WriteLine("var chip = document.getElementById(chipId);");
                html.WriteLine("chip.style.zIndex = z;");
                html.WriteLine("var rect = chip.getBoundingClientRect()");
                html.WriteLine("thingsMoving.push({chip: chip, startX: rect.left, startY: rect.top, endX: x, endY: y, endScale: scale, endRotation: rotation});");
                html.WriteLine("window.requestAnimationFrame(animateFrame);");
                html.Indent--;
                html.WriteLine("}");

                while (player > 0 || score.All(s => s < winningScore))
                {
                    if (turn == 25 && !writeExample)
                    {
                        // Draw table image.
                        using var table = new SKBitmap((int)(36 * dpi), (int)(36 * dpi));
                        using (var canvas = new SKCanvas(table))
                        {
                            const float margin = cutWidth * .1f;
                            canvas.Translate(table.Width / 2 - (cutWidth * cardsPerRow + margin * (cardsPerRow - 1)) / 2, table.Height / 2 - (cutHeight * cardRows + margin * (cardRows - 1)) / 2);

                            for (var i = 0; i < tableau.Length; i++)
                            {
                                var x = (cutWidth + margin) * (i % cardsPerRow);
                                var y = (cutHeight + margin) * (i / cardsPerRow);
                                using var b = SKBitmap.Decode($"cut/card{tableau[i]?.Id}.png");
                                var save = canvas.TotalMatrix;
                                canvas.Translate(x + cutWidth / 2, y + cutHeight / 2);
                                canvas.RotateDegrees(tableau[i]?.Angle ?? 0f);
                                canvas.Translate(- x - cutWidth / 2, - y - cutHeight / 2);
                                canvas.DrawBitmap(b, x, y);
                                canvas.SetMatrix(save);
                            }

                            canvas.ResetMatrix();

                            for (var p = 0; p < players; p++)
                            {
                                canvas.Translate(table.Width / 2, table.Height / 2);
                                canvas.RotateDegrees(360 / players);
                                canvas.Translate(-table.Width / 2, -table.Height / 2);
                                var groups = cards[p].GroupBy(c => c.Color).ToList();
                                var x = (table.Width - groups.Count * (cutWidth + margin) + (groups.Count - 1) * margin) / 2f;

                                foreach (var group in groups)
                                {
                                    var y = table.Height * .75f;

                                    foreach (var card in group.OrderBy(c => c.Acquision))
                                    {
                                        var save = canvas.TotalMatrix;
                                        canvas.Translate(x + cutWidth / 2, y + cutHeight / 2);
                                        canvas.RotateDegrees(card?.Angle ?? 0f);
                                        canvas.Translate(- x - cutWidth / 2, - y - cutHeight / 2);
                                        using var b = SKBitmap.Decode($"cut/card{card?.Id}.png");
                                        canvas.DrawBitmap(b, x, y);
                                        canvas.SetMatrix(save);
                                        y += cutHeight * .2f;
                                    }

                                    x += cutWidth + margin;
                                }
                            }
                        }

                        table.Save($"gen/table{players}.png");
                    }

                    if (player == 0)
                    {
                        if (turn > buyTurn + 100)
                        {
                            lock (debugTableauLock)
                            {
                                DumpTableau(tableau, rand, cardRows, "gen/debug-tableau.png");
                            }
                            //Debugger.Break();
                            Interlocked.Increment(ref voidGames);
                            break;
                        }

                        writer.WriteLine($"\nTurn {turn}");
                    }

                    Card? targetCard = null;
                    var best = double.MaxValue;
                    int[] chipsNeeded;
                    html.WriteLine($"function turn{turn}{names[player][0]}() {{");
                    html.Indent++;

                    if (player == 0 && turn == 1)
                    {
                        for (var p = 0; p < players; p++)
                        {
                            html.WriteLine($"var rect{p} = document.getElementById('player{p}').getBoundingClientRect();");
                            html.WriteLine($"playerX[{p}] = rect{p}.left;");
                            html.WriteLine($"playerY[{p}] = rect{p}.top;");
                        }
                    }

                    var message = new StringBuilder();
                    int[] excess;

                    foreach (var card in tableau.Where(c => c is not null).Cast<Card>().OrderByDescending(c => c.Points))
                    {
                        if (player == 0 && card.Points == 0)
                            continue;

                        if (players.Enumerate().Where(p => p != player).Select(p => target[p]).Any(c => c?.Id == card.Id))
                            continue;

                        chipsNeeded = card.NetCost(cards[player], chips[player]);
                        var canBuy = colors.Enumerate().All(c => bank[c] >= chipsNeeded[c]);

                        if (!canBuy)
                            continue;

                        excess = colors.InitArray(c => Math.Max(0, chips[player][c] - Math.Max(0, card.Cost[c] - cards[player].Sum(k => k.Color == c ? k.CouponValue : 0))));

                        if (chips[player].Sum() + chipsNeeded.Sum() - excess.Sum() > chipLimit)
                            continue;

                        var turnsNeeded = Math.Max(chipsNeeded.Max(), (chipsNeeded.Sum() + chipsPerTurn - 1) / chipsPerTurn) + 0.01 * chipsNeeded.Sum();
                        var colorsNeeded = chipsNeeded.Count(c => c > 0);
                        var colorsAvailable = chipsNeeded.Select((c, i) => c > 0 && bank[i] > 0).Count(b => b);

                        if (turnsNeeded < best && colorsAvailable >= Math.Min(3, colorsNeeded))
                        {
                            best = turnsNeeded;
                            targetCard = card;
                        }
                    }

                    target[player] = targetCard;

                    if (targetCard == null)
                    {
                        // DumpTableau(tableau, rand, "gen/debug-tableau.png");
                        // Debugger.Break();
                        targetCard = tableau.Except(target).Where(c => c is not null).Cast<Card>().ToList().Scramble(rand).First();
                    }

                    chipsNeeded = targetCard.NetCost(cards[player], chips[player]);
                    excess = colors.InitArray(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Sum(k => k.Color == c ? k.CouponValue : 0))));
                    var chipCounts = new int[colors];
                    List<string> chipStr;

                    int GetPlayX(int color) =>
                        color * (int)(tw * 1.1 * .5);

                    List<string> ChipDescriptions(int[] chipCounts) =>
                        chipCounts.Select((c, i) => c == 0 ? "" : c == 1 ? colorSingular[i] : ordinalName[c] + " " + colorNames[i]).Where(s => s.Length > 0).ToList();

                    if (chipsNeeded.Sum() <= excess.Sum() / 3)
                    {
                        writer.Write($"{names[player]}");
                        message.Append(names[player]);
                        var wild = 0;
                        int x, y;
                        var noChips = true;

                        if (chipsNeeded.Sum() > 0)
                        {
                            noChips = false;
                            writer.Write(" turns in");

                            while (chipsNeeded.Sum() > wild)
                            {
                                for (var w = 0; w < 3; w++)
                                {
                                    while (true)
                                    {
                                        var i = rand.Next(colors);

                                        if (excess[i] > 0)
                                        {
                                            chipCounts[i]++;
                                            excess[i]--;
                                            chips[player][i]--;
                                            writer.Write($" {colorNames[i]}");
                                            var chipId = playerChipIds[player][i].Pop();
                                            chipIds[i].Push(chipId);
                                            GetBankXY(i, out x, out y);
                                            html.WriteLine($"animate('{chipId}', {x}, {y}, 1, 0, {bank[i]});");
                                            bank[i]++;
                                            break;
                                        }
                                    }
                                }

                                wild++;
                            }

                            writer.Write(" for wildcard and");
                            wildcardHappened = true;
                            message.Append(" turns in ");
                            message.Append(ChipDescriptions(chipCounts).AsEnglish());
                            message.Append(" to use as ");
                            message.Append(wild == 1 ? "a wildcard" : ordinalName[wild] + " wildcards");
                            message.Append(" and");
                        }

                        buyTurn = turn;
                        writer.Write($" buys {targetCard.Name}");
                        Array.Clear(chipCounts);
                        var effective = targetCard.EffectiveCost(cards[player]);

                        for (var c = 0; c < colors; c++)
                        {
                            if (cards[player].Any(k => k.Color == c && k.CouponValue > 1))
                                doubleCouponHappened = true;

                            var pay = effective[c];

                            if (pay > 0)
                                noChips = false;

                            while (pay > chips[player][c] && wild > 0)
                            {
                                pay--;
                                wild--;
                            }

                            if (pay > chips[player][c])
                                Debugger.Break();

                            chipCounts[c] = pay;

                            for (var p = 0; p < pay; p++)
                            {
                                --chips[player][c];
                                var chipId = playerChipIds[player][c].Pop();
                                chipIds[c].Push(chipId);
                                GetBankXY(c, out x, out y);
                                html.WriteLine($"animate('{chipId}', {x}, {y}, 1, 0, {bank[c]});");
                                ++bank[c];
                            }
                        }

                        message.Append(" invests ");
                        chipStr = ChipDescriptions(chipCounts);
                        message.Append(chipStr.Count == 0 ? (noChips ? "no chips" : "no additional chips") : chipStr.AsEnglish());
                        message.Append(" to attract ");
                        message.Append(targetCard.Name);
                        message.Append($" to {names[player]}ville");

                        cards[player].Add(targetCard.Stamped());
                        target[player] = null;
                        var z = cards[player].Count(c => c.Color == targetCard.Color);
                        x = GetPlayX(targetCard.Color) - tw / 4;
                        y = 12 * z - th / 4;
                        html.WriteLine($"animate('card{targetCard.Id}', {x} + playerX[{player}], {y} + playerY[{player}], .5, {targetCard.Angle:F2}, {z});");

                        if (targetCard.Points > 0)
                        {
                            score[player] += targetCard.Points;
                            writer.Write($" and scores {targetCard.Points}");

                            if (noChips && targetCard.Bonus > 0)
                            {
                                score[player] += targetCard.Bonus;
                                writer.Write($" with +{targetCard.Bonus} bonus");
                                message.Append($" and earns {ordinalName[targetCard.Bonus]} bonus points");
                                html.WriteLine($"document.getElementById('bonus{player}').innerHTML += '<div class=bonus>+{targetCard.Bonus}</div>';");
                                bonusEarned += targetCard.Bonus;

                                switch (targetCard.Bonus)
                                {
                                    case 2: twosEarned++; break;
                                    case 3: threesEarned++; break;
                                    case 4: twosEarned += 2; break;
                                    default: throw new NotImplementedException();
                                }
                            }

                            writer.WriteLine($" for a total of {score[player]}");
                            html.WriteLine($"document.getElementById('score{player}').innerHTML = '{score[player]}';");
                        }

                        message.Append(".");
                        var index = tableau.Length.Enumerate().Single(i => tableau[i]?.Id == targetCard.Id);

                        if (draw.Any())
                        {
                            tableau[index] = Draw(draw, rand);
                            writer.WriteLine($"Card is replaced with {tableau[index]?.Name}");
                            message.Append($" They then draw {tableau[index]?.Name}.");
                            GetTableauXY(index, out x, out y);
                            html.WriteLine($"animate('card{tableau[index]?.Id}', {x}, {y}, 1, {tableau[index]?.Angle:F2}, 0);");
                        }
                        else
                        {
                            tableau[index] = null;
                            writer.WriteLine("Card is not replaced");
                        }
                        // DumpTableau(tableau, rand, "gen/debug-tableau.png");
                    }
                    else
                    {
                        var picks = new HashSet<int>();
                        writer.Write($"{names[player]} takes chips");
                        Array.Clear(chipCounts);
                        var wasNeeded = (int[])chipsNeeded.Clone();
                        var extraChips = new int[colors];

                        for (var i = 0; i < chipsPerTurn; i++)
                        {
                            excess = colors.InitArray(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Sum(k => k.Color == c ? k.CouponValue : 0))));

                            if (chips[player].Sum() >= chipLimit + excess.Sum())
                                break;

                            var choices = colors.Enumerate().Where(i => !picks.Contains(i) && bank[i] > 0).ToList();

                            if (!choices.Any())
                                break;

                            var j = -1;
                            var max = choices.Max(i => chipsNeeded[i]);

                            if (max > 0)
                            {
                                j = colors.Enumerate().Cast<int?>().ToList().Scramble(rand).FirstOrDefault(c => c is not null && bank[c.Value] > 0 && chipsNeeded[c.Value] == max && !picks.Contains(c.Value)) ?? -1;
                            }

                            if (j == -1)
                            {
                                var cc = colors.Enumerate().Cast<int?>().ToList().Scramble(rand);
                                j = cc.FirstOrDefault(c => c is not null && !picks.Contains(c.Value) && bank[c.Value] > 0 && chipsNeeded[c.Value] > 0) ?? -1;
                            }

                            if (j == -1)
                            {
                                if (chips[player].Sum() >= chipLimit)
                                    break;

                                var cc = colors.Enumerate().Cast<int?>().ToList().Scramble(rand);
                                j = cc.FirstOrDefault(c => c is not null && !picks.Contains(c.Value) && bank[c.Value] > 0) ?? -1;

                                if (j != -1)
                                {
                                    extraChips[j]++;
                                    extraChipsHappened = true;
                                }
                            }
                            else
                            {
                                chipCounts[j]++;
                            }

                            picks.Add(j);
                            if (chipsNeeded[j] > 0) chipsNeeded[j]--;

                            if (bank[j] == 0)
                                Debugger.Break();

                            bank[j]--;
                            writer.Write($" {colorNames[j]}");
                            var chipId = chipIds[j].Pop();
                            playerChipIds[player][j].Push(chipId);
                            var x = GetPlayX(j) + tw / 4 - csize / 2;
                            var y = -4 * chips[player][j] - csize;
                            html.WriteLine($"animate('{chipId}', {x} + playerX[{player}], {y} + playerY[{player}], .5, 0, {chips[player][j]});");
                            chips[player][j]++;
                        }

                        message.Append($"{names[player]} takes ");
                        chipStr = ChipDescriptions(chipCounts);
                        message.Append(chipStr.Count == 0 ? "no chips" : chipStr.AsEnglish());
                        message.Append($", intending to attract {targetCard.Name}");

                        if (extraChips.Sum() > 0)
                        {
                            message.Append(", and ");
                            message.Append(ChipDescriptions(extraChips).AsEnglish());
                            message.Append(" for later");
                        }

                        if (chips[player].Sum() > chipLimit)
                        {
                            writer.Write(" and returns");
                            returnHappened = true;
                            Array.Clear(chipCounts);

                            while (chips[player].Sum() > chipLimit)
                            {
                                excess = colors.InitArray(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Sum(k => k.Color == c ? k.CouponValue : 0))));

                                if (excess.Sum() == 0)
                                    excess = (int[])chips[player].Clone();

                                var extra = colors.Enumerate().Where(c => excess[c] > 0).ToList().Scramble(rand).First();
                                writer.Write($" {colorNames[extra]}");

                                if (chips[player][extra] == 0)
                                    Debugger.Break();

                                var chipId = playerChipIds[player][extra].Pop();
                                chipIds[extra].Push(chipId);
                                GetBankXY(extra, out var x, out var y);
                                html.WriteLine($"animate('{chipId}', {x}, {y}, 1, 0, {bank[extra]});");
                                chips[player][extra]--;
                                bank[extra]++;
                                chipCounts[extra]++;
                            }

                            message.Append($". They have more than {chipLimit} chips and decide to return ");
                            message.Append(ChipDescriptions(chipCounts).AsEnglish());
                        }

                        writer.WriteLine();
                        message.Append(".");
                    }

                    player = (player + 1) % players;
                    var gameOver = player == 0 && score.Any(s => s >= winningScore);

                    if (gameOver)
                    {
                        var winner = players.Enumerate().MaxBy(p => score[p]);
                        message.Append($"<br><br>{names[winner]} wins the game with a score of {score[winner]}!");
                    }

                    html.WriteLine($"message.innerHTML = '{message}';");

                    if (player == 0)
                    {
                        writer.Write("Bank has");

                        for (var c = 0; c < colors; c++)
                            writer.Write($" {bank[c]} {colorNames[c]},");

                        writer.WriteLine();
                        turn++;
                    }

                    if (player == 0 && score.Any(s => s >= winningScore))
                    {
                        html.WriteLine("advance.innerHTML = 'Game over!';");
                        html.WriteLine("advance.disabled = true;");
                        tieHappened = score.Count(s => s == score.Max()) > 1;
                    }
                    else
                    {
                        if (player == 1 && turn == 1)
                        {
                            html.WriteLine($"advance.innerHTML = 'Next';");
                        }

                        html.WriteLine($"advance.onclick = turn{turn}{names[player][0]};");
                    }

                    html.Indent--;
                    html.WriteLine("}"); // end of script for player turn
                }

                lock (debugTableauLock)
                {
                    if (twosEarned > mostPlus2Earned || threesEarned > mostPlus3Earned || bonusEarned > mostBonusEarned)
                    {
                        mostPlus2Earned = Math.Max(twosEarned, mostPlus2Earned);
                        mostPlus3Earned = Math.Max(threesEarned, mostPlus3Earned);
                        mostBonusEarned = Math.Max(bonusEarned, mostBonusEarned);
                        Console.WriteLine($"{mostPlus2Earned}\t{mostPlus3Earned}\t{mostBonusEarned}");
                    }
                }

                html.Indent--;
                html.WriteLine("</script>");
                html.Indent--;
                html.WriteLine("</body>");
                html.Indent--;
                html.WriteLine("</html>");

                if (players == 2 && writeExample)
                {
                    html.Close();
                    Directory.CreateDirectory("gen/games");
                    File.Copy("gen/example.html", $"gen/games/game-{score[0]}-{score[1]}.html", true);
                }
            }

            var bonuses = players.Enumerate().Sum(p => score[p] - cards[p].Sum(c => c.Points));

            if (writeExample && bonuses > 0 && players == 2 && wildcardHappened && returnHappened && extraChipsHappened && doubleCouponHappened && !tieHappened && turn < 40)
                break;

            Interlocked.Increment(ref gameLengths[players][turn - 1]);
        }
    }

    T Draw<T>(List<T> items, Random rand)
    {
        var index = rand.Next(items.Count);
        var item = items[index];
        items.RemoveAt(index);
        return item;
    }

    IEnumerable<List<int>> LyndonWords(int n, int k)
    {
        var w = new List<int>(new int[n]);

        while (true)
        {
            if (w.Count == n)
                yield return new List<int>(w);

            var c = w.Count;

            while (w.Count < n)
                w.Add(w[^c]);

            while (w.Any() && w.Last() == k - 1)
                w.RemoveAt(w.Count - 1);

            if (!w.Any())
                yield break;

            ++w[^1];
        }
    }
}