#define COST_SHIFT
#define xGAME_CRAFTER

using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using SkiaSharp;

record CardInfo(int Id, int Index, int[] Cost, int Points, int Bonus);

class Program
{
    static void Main()
    {
        var p = new Program();
        p.Run();
    }

    static void AllCores(Action<int> action)
    {
        Parallel.ForEach(Enumerable.Range(0, Environment.ProcessorCount), action);
    }

    void Run()
    {
        const int colors = 6;
        const int maxColorsPerCard = 4;

        const double dpi = 300;
        const double pixelsPerMillimeter = dpi / 25.4;
        const double pixelsPerPoint = dpi / 72.0;

        const int cutWidth = (int)(63 * pixelsPerMillimeter + .5);
        const int cutHeight = (int)(88 * pixelsPerMillimeter + .5);

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

        var sectors = new List<string>();
        var colorScheme = new List<SKColor>();
        var cardPaint = new List<SKPaint>();

        "sectors.txt".SplitLines(' ', cols =>
        {
            sectors.Add(cols[0]);
            var color = SKColor.Parse(cols[1]);
            colorScheme.Add(color);
            cardPaint.Add(new SKPaint { Color = color.WithAlpha(128) });
        });

        var wordIndex = 0;
        var deck = new List<List<int>>();

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

            // Determine cost associated with 1s.
            var baseCost = (wordIndex + 3) / 4;

            // Add 50% for the cost of 2s.
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
                deck.Add(card.Skip(colors - j).Take(j).Concat(card.Take(colors - j)).ToList());
#else                
                deck.Add(card.Skip(colors - i).Take(i).Concat(card.Take(colors - i)).ToList());
#endif
            }
        }

        // Keep track of how early each card is purchased in simulations.
        var firstBuys = Enumerable.Range(0, deck.Count).Select(c => new ConcurrentBag<int>()).ToArray();

        // Run simulations on all processors.
        AllCores(pid =>
        {
            var rand = new Random(99169 + pid);

            for (var game = 0; game < 10_000 / Environment.ProcessorCount; game++)
            {
                var deal = new List<List<int>>(deck);
                var tableau = new List<List<int>>();

                for (var i = 0; i < 15; i++)
                    tableau.Add(Draw(deal, rand));

                const int players = 2;
                var turn = 0;
                var chips = Enumerable.Range(0, players).Select(p => new int[colors]).ToArray();
                var coupons = Enumerable.Range(0, players).Select(p => new int[colors]).ToArray();
                var score = new int[players];

                while (deal.Any() || tableau.Any())
                {
                    turn++;

                    for (var p = 0; p < players; p++)
                    {
                        var affordable = Enumerable.Range(0, tableau.Count).Where(c => Enumerable.Range(0, colors).All(i => tableau[c][i] <= chips[p][i] + coupons[p][i])).ToList();
                        var maxColors = Enumerable.Range(0, colors).Select(c => tableau.Max(t => t[c])).ToList();
                        var colorsDisallowed = Enumerable.Range(0, colors).Where(c => maxColors[c] <= chips[p][c] + coupons[p][c]).ToList();

                        if ((rand.Next(2) == 0 && colorsDisallowed.Count <= 3) || !affordable.Any())
                        {
                            // Take chips
                            var choices = Enumerable.Range(0, colors).Where(c => chips[p][c] > 0 && rand.Next(2) == 0).ToList();
                            var others = Enumerable.Range(0, colors).Except(choices).ToList();
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
                            var cardIndex = deck.FindIndex(c => Enumerable.SequenceEqual(c, tc));
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

        for (var i = 0; i < deck.Count / colors; i++)
        {
            var mins = Enumerable.Range(i * colors, colors).Select(j => firstBuys[j].Min()).ToList();
            var avg = mins.Average();
            bySpeed.Add((i, avg));
        }

        var deckInfo = new List<CardInfo>();
        var cardId = 0;

        foreach (var (i, avg) in bySpeed.OrderBy(_ => _.Item2).ThenBy(_ => deck[_.Item1 * colors].Sum()))
        {
            var points = Math.Min(5, (int)Math.Round(avg) - 3);
            Console.Write($"{i + 1,2}: {avg:F2}\t");
            var card = deck[i * colors];
            card.Write(Console.Out, " ");
            Console.Write($"\t{points}");
            var bonus = points > 0 && card.Where(c => c > 0).Min() >= 3 && card.Sum() >= 6 ? (card.Max() + 1) / 2 : 0;

            if (bonus > 0)
                Console.Write($" (+{bonus})");

            Console.WriteLine();
            deckInfo.Add(new CardInfo(cardId++, i, card.ToArray(), points, bonus));
        }

        deckInfo = deckInfo.OrderBy(d => d.Points).ThenBy(d => d.Cost.Sum()).ToList();

        var sectorNum = 0;
        var names = sectors.ToDictionary(s => s, s => new List<string>());

        "names.txt".SplitLines('\t', tabs =>
        {
            if (tabs.Count() >= 2)
                names[tabs[0]].Add(tabs[1]);
        });

        var discImage = new Dictionary<string, SKBitmap>();
        var insetImage = new Dictionary<string, SKBitmap>();
        var smooth = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };

        SKColor Lerp(SKColor a, SKColor b, float t) =>
            new(
                (byte)(a.Red * (1 - t) + b.Red * t),
                (byte)(a.Green * (1 - t) + b.Green * t),
                (byte)(a.Blue * (1 - t) + b.Blue * t));

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
                Save(bmp, $"gen/{sector}-chip.png");

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
            }

            sectorNum++;
        }

        var gammaMap = ColorMap.FromGamma(2.0);

        var pointsPaint = new SKPaint
        {
            Color = SKColors.Black,
            Typeface = SKTypeface.FromFamilyName("Stencil", SKFontStyle.Bold),
            TextAlign = SKTextAlign.Right,
            TextSize = 24 * (float)pixelsPerPoint,
            IsAntialias = true
        };

        var bonusPaint = new SKPaint
        {
            Color = SKColors.Black,
            Typeface = SKTypeface.FromFamilyName("Stencil", SKFontStyle.Bold),
            TextAlign = SKTextAlign.Right,
            TextSize = 12 * (float)pixelsPerPoint,
            IsAntialias = true
        };

        var namePaint = new SKPaint
        {
            Color = SKColors.Black,
            Typeface = SKTypeface.FromFamilyName("Candara", SKFontStyle.Bold),
            TextAlign = SKTextAlign.Center,
            TextSize = 12 * (float)pixelsPerPoint,
            IsAntialias = true
        };

        // Generate each card-front image.
        Parallel.ForEach(deckInfo, cardInfo =>
        {
            for (var couponColor = 0; couponColor < colors; couponColor++)
            {
                var cardNum = cardInfo.Id * colors + couponColor;
                var card = deck[cardInfo.Index * colors + couponColor];
                var pngFileName = $"cards/card{cardNum}.png";
                var thumbFileName = $"thumb/card{cardNum}.png"; // Trimmed and corner-rounded small images.
                var currentDir = Directory.GetCurrentDirectory();

                using (var skbmp = new SKBitmap(cardWidth, cardHeight))
                using (var canvas = new SKCanvas(skbmp))
                {
                    var sector = sectors[couponColor];
                    var color = colorScheme[couponColor];

                    // Card background image.
                    canvas.DrawBitmap(insetImage[sector], new SKRect(0, 0, cardWidth, cardHeight), smooth);

                    // Card color overlay tint.
                    canvas.DrawRect(new SKRect(0, 0, cardWidth, cardHeight), cardPaint[couponColor]);

                    const int marginX = bleedX + safeArea;
                    const int marginY = bleedY + safeArea;
                    const double couponX = marginX + (bigDotRadius + dotPenSize / 2) * 1.5;
                    const double couponY = marginY + (bigDotRadius + dotPenSize / 2) * 1.5;

                    // Coupon symbol.
                    canvas.DrawBitmap(discImage[sector], new SKRect((int)(couponX - bigDotRadius * 1.5), (int)(couponY - bigDotRadius * 1.5), (int)(couponX + bigDotRadius * 1.5), (int)(couponY + bigDotRadius * 1.5)), smooth);

                    // Point value.
                    float cursorY = marginY;

                    if (cardInfo.Points > 0)
                    {
                        cursorY += pointsPaint.TextSize - 20;

                        canvas.DrawText(
                            cardInfo.Points.ToString(),
                            cardWidth - marginX,
                            cursorY,
                            pointsPaint);
                    }

                    // Bonus point value.
                    if (cardInfo.Bonus > 0)
                    {
                        cursorY += bonusPaint.TextSize * 1.5f;

                        canvas.DrawText(
                            $"(+{cardInfo.Bonus})",
                            cardWidth - marginX,
                            cursorY,
                            bonusPaint);
                    }

                    // Cost symbols.
                    const int maxPerCol = 5;
                    var dotCount = card.Count(c => c > 0) + card.Count(c => c > maxPerCol);
                    const double dotSpacing = bigDotRadius * 2.5;
                    var x0 = centerX - (dotCount - 1) * dotSpacing / 2;
                    var x = x0;

                    for (var i = 0; i < colors; i++)
                    {
                        var yc = card[i] > maxPerCol ? (card[i] + 1) / 2 : card[i];
                        var y = centerY - (yc - 1) * dotSpacing / 2;

                        for (var j = 0; j < card[i]; j++)
                        {
                            canvas.DrawBitmap(discImage[sectors[i]], new SKRect((int)(x - bigDotRadius), (int)(y - bigDotRadius), (int)(x + bigDotRadius), (int)(y + bigDotRadius)), smooth);

                            y += dotSpacing;

                            if (card[i] > maxPerCol && j + 1 == yc)
                            {
                                y = centerY - (card[i] - yc - 1) * dotSpacing / 2;
                                x += dotSpacing;
                            }
                        }

                        if (card[i] > 0)
                        {
                            x += dotSpacing;
                        }
                    }

                    // Business name.
                    var name = names[sector][cardInfo.Id];

                    if (namePaint.MeasureText(name) > cutWidth)
                        Debugger.Break();

                    canvas.DrawText(
                        name,
                        centerX,
                        cardHeight - bleedY - safeArea - 20,
                        namePaint);

                    // Gamma correction.
                    gammaMap.Map(skbmp);

                    // Save.
                    Save(skbmp, pngFileName);

                    // Create thumbnail image with trimmed edges and rounded corners.
                    // Trim.
                    using var rounded = skbmp.Crop(cutWidth, cutHeight); // new SKBitmap(cutWidth, cutHeight);

                    // Round.
                    var cornerR = safeArea;
                    var bx0 = safeArea - 1;
                    var bx1 = cutWidth - safeArea;
                    var y0 = safeArea - 1;
                    var y1 = cutHeight - safeArea;

                    for (var bx = 0; bx < cornerR; bx++)
                    {
                        for (var y = 0; y < cornerR; y++)
                        {
                            var r = Math.Sqrt(bx * bx + y * y);
                            var alpha = r < cornerR - 1 ? 255 : r > cornerR ? 0 : (int)(255 * (cornerR - r));

                            void UpdateAlpha(int x, int y)
                            {
                                var pixel = rounded.GetPixel(x, y);
                                pixel = pixel.WithAlpha((byte)alpha);
                                rounded.SetPixel(x, y, pixel);
                            }

                            if (alpha < 255)
                            {
                                UpdateAlpha(bx0 - bx, y0 - y);
                                UpdateAlpha(bx0 - bx, y1 + y);
                                UpdateAlpha(bx1 + bx, y0 - y);
                                UpdateAlpha(bx1 + bx, y1 + y);
                            }
                        }
                    }

                    // Shrink.
                    using var thumb = rounded.Resize(rounded.Width / 3, rounded.Height / 3);
                    Save(thumb, thumbFileName);
                }
            }
        });

        var thScale = 0.25;
        var gridX = 5;
        var gridY = 3;
        var gridMargin = (float)(cutWidth * thScale * 0.2);
        var thWidth = (float)(cutWidth * thScale);
        var thHeight = (float)(cutHeight * thScale);
        var imageInfo = new SKImageInfo((int)Math.Ceiling((thWidth + gridMargin) * gridX), (int)Math.Ceiling((thHeight + gridMargin) * gridY), SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var rand = new Random(99169);

        using (var bmp = new SKBitmap((int)Math.Ceiling((thWidth + gridMargin) * gridX), (int)Math.Ceiling((thHeight + gridMargin) * gridY)))
        {
            var ids = new int[gridX * gridY];

            for (var i = 0; i < ids.Length; i++)
            {
                do
                {
                    ids[i] = rand.Next(deck.Count);
                }
                while (ids.Take(i).Any(_ => _ == ids[i]) || (i >= gridX * (gridY - 1) && deckInfo[ids[i] / colors].Points != 0));
            }

            var canvas = new SKCanvas(bmp);

            for (var y = 0; y < gridY; y++)
            {
                for (var x = 0; x < gridX; x++)
                {
                    var px = x * (thWidth + gridMargin) + gridMargin / 2;
                    var py = y * (thHeight + gridMargin) + gridMargin / 2;
                    var angle = rand.Next(6) - 3;

                    using var cardBitmap = SKBitmap.Decode($"thumb/card{ids[y * gridX + x]}.png");
                    canvas.Translate(px + thWidth / 2, py + thHeight / 2);
                    canvas.RotateDegrees(angle);
                    canvas.Translate(-px - thWidth / 2, -py - thHeight / 2);
                    canvas.DrawBitmap(cardBitmap, SKRect.Create(px, py, thWidth, thHeight), smooth);
                    canvas.ResetMatrix();
                }
            }

            canvas.Flush();
            Save(bmp, "gen/tableau.png");
        }

        /*
        gridX = colors;
        rand = new Random();
        const int players = 4;
        List<int>[] playerCards;

        while (true)
        {
            var deal = Enumerable.Range(0, deck.Count).ToList();
            var scores = new int[players];
            playerCards = Enumerable.Range(0, players).Select(_ => new List<int>()).ToArray();
            var coupons = Enumerable.Range(0, players).Select(_ => new int[colors]).ToArray();
            
            while (scores.All(s => s < 25))
            {
                var cp = rand.Next(players);
                var id = Draw(deal, rand);
                playerCards[cp].Add(id);
                var card = deck[id];
                var info = deckInfo[id / colors];
                scores[cp] += info.Points;

                if (Enumerable.Range(0, colors).All(i => coupons[cp][i] >= card[i]))
                    scores[cp] += info.Bonus;

                coupons[cp][id % colors]++;
            }

            if (scores.Min() >= 15)
                break;
        }

        for (var player = 0; player < players; player++)
        {
            using (var bmp = new SKBitmap((int)Math.Ceiling((thWidth + gridMargin) * gridX), (int)Math.Ceiling((thHeight + gridMargin) * gridY)))
            {
                var canvas = new SKCanvas(bmp);
                var ids = playerCards[player];
                var ix = 0;

                for (var x = 0; x < gridX; x++)
                {
                    var cards = ids.Where(id => (id % colors) == x).OrderBy(id => deckInfo[id / colors].Points).ToList();

                    for (var y = 0; y < cards.Count; y++)
                    {
                        var id = cards[y];
                        var px = ix * (thWidth + gridMargin) + gridMargin / 2 + rand.Next(21) - 10;
                        var py = y * (thHeight / 5) + gridMargin / 2;
                        var angle = rand.Next(6) - 3;

                        using var cardBitmap = SKBitmap.Decode($"thumb/card{id}.png");
                        canvas.Translate(px + thWidth / 2, py + thHeight / 2);
                        canvas.RotateDegrees(angle);
                        canvas.Translate(-px - thWidth / 2, -py - thHeight / 2);
                        canvas.DrawBitmap(cardBitmap, SKRect.Create(px, py, thWidth, thHeight), smooth);
                        canvas.ResetMatrix();
                    }

                    if (cards.Any())
                        ix++;
                }

                canvas.Flush();
                Save(bmp, $"gen/hand{player + 1}.png");
            }
        }
        */
    }

    void Save(SKBitmap bitmap, string filename)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 80);
        using var stream = File.OpenWrite(filename);
        data.SaveTo(stream);
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