#define COST_SHIFT
#define xGAME_CRAFTER

using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text;
using SkiaSharp;

record CardInfo(int Id, int Index, int[] Cost, int Points, int Bonus);

record Card(int Id, int[] Cost, int Points, int Bonus, int Color, string Name)
{
    public double Angle = (Id % 5) - 2;

    public DateTime Acquision { get; private set; } = DateTime.UtcNow;

    public Card Stamped()
    {
        return new Card(Id, Cost, Points, Bonus, Color, Name);
    }
}

class Program
{
    const int colors = 6;
    const double dpi = 300;
    const double pixelsPerMillimeter = dpi / 25.4;
    const double pixelsPerPoint = dpi / 72.0;
    const int cutWidth = (int)(63 * pixelsPerMillimeter + .5);
    const int cutHeight = (int)(88 * pixelsPerMillimeter + .5);

    static readonly SKPaint smooth = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };


    static void Main()
    {
        var p = new Program();
        p.Run();
    }

    static void OnAllCores(Action<int> action)
    {
        Parallel.ForEach(Enumerable.Range(0, Environment.ProcessorCount), action);
    }

    void Run()
    {
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
                cardCosts.Add(card.Skip(colors - j).Take(j).Concat(card.Take(colors - j)).ToArray());
#else                
                cardCosts.Add(card.Skip(colors - i).Take(i).Concat(card.Take(colors - i)).ToArray());
#endif
            }
        }

        // Keep track of how early each card is purchased in simulations.
        var firstBuys = Enumerable.Range(0, cardCosts.Count).Select(c => new ConcurrentBag<int>()).ToArray();

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
            var points = Math.Min(5, (int)Math.Round(avg) - 3);
            Console.Write($"{i + 1,2}: {avg:F2}\t");
            var card = cardCosts[i * colors];
            card.Write(Console.Out, " ");
            Console.Write($"\t{points}");
            var bonus = points > 0 && card.Where(c => c > 0).Min() >= 3 && card.Sum() >= 6 ? (card.Max() + 1) / 2 : 0;

            if (bonus > 0)
                Console.Write($" (+{bonus})");

            Console.WriteLine();
            deckInfo.Add(new CardInfo(cardId, i, card.ToArray(), points, bonus));

            for (var c = 0; c < colors; c++)
            {
                var index = i * colors + c;
                deck.Add(new Card(deck.Count, cardCosts[index], points, bonus, c, names[sectors[c]][cardId]));
            }

            cardId++;
        }

        Directory.CreateDirectory("gen");

        using (var writer = File.CreateText("gen/deck.txt"))
        {
            writer.Write("Id\tColor\tPoints\tBonus");

            for (var c = 0; c < colors; c++)
                writer.Write($"\tCost{c}");

            writer.WriteLine("\tName");

            foreach (var card in deck)
            {
                writer.Write($"{card.Id}\t{card.Color}\t{card.Points}\t{card.Bonus}");

                foreach (var c in card.Cost)
                    writer.Write($"\t{c}");

                writer.WriteLine($"\t{card.Name}");
            }
        }

        var sectorNum = 0;
        var discImage = new Dictionary<string, SKBitmap>();
        var insetImage = new Dictionary<string, SKBitmap>();

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
            }

            sectorNum++;
        }

        for (var c = 0; c < 2; c++)
        {
            $"art/character{c}.png".AsBitmap().Resize(100, 100).Save($"gen/character{c}.png");
        }

        var gammaMap = ColorMap.FromGamma(1.0);

        SKPaint GetFont(string family, SKFontStyle style, SKTextAlign align, float points) =>
            new() { Color = SKColors.Black, IsAntialias = true, Typeface = SKTypeface.FromFamilyName(family, style), TextAlign = align, TextSize = points * (float)pixelsPerPoint };

        var pointsPaint = GetFont("Stencil", SKFontStyle.Bold, SKTextAlign.Right, 24f);
        var bonusPaint = GetFont("Stencil", SKFontStyle.Bold, SKTextAlign.Right, 12f);
        var namePaint = GetFont("Candara", SKFontStyle.Bold, SKTextAlign.Center, 12f);
        var idPaint = GetFont("Candara", SKFontStyle.Normal, SKTextAlign.Left, 3f);

        Directory.CreateDirectory("cards");
        Directory.CreateDirectory("thumb");

        // Generate each card-front image.
        Parallel.ForEach(deck, card =>
        {
            using (var skbmp = new SKBitmap(cardWidth, cardHeight))
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

                    // Coupon symbol.
                    canvas.DrawBitmap(discImage[sector], new SKRect((int)(couponX - bigDotRadius * 1.5), (int)(couponY - bigDotRadius * 1.5), (int)(couponX + bigDotRadius * 1.5), (int)(couponY + bigDotRadius * 1.5)), smooth);

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

                // Save.
                skbmp.Save($"cards/card{card.Id}.png");

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

                // Shrink.
                using var thumb = rounded.Resize(rounded.Width / 3, rounded.Height / 3);
                thumb.Save($"thumb/card{card.Id}.png");
            }
        });

        var rand = new Random(99169 * 5);
        var draw = new List<Card>(deck);
        DumpTableau(NewDeal(draw, rand, 3, 5), rand, 3, "gen/tableau.png");
        ExampleGame(deck, sectors);
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

    void ExampleGame(List<Card> deck, List<string> sectors)
    {
        var bankInit = new[] { 0, 0, 5, 6, 7, 8 };
        var names = new[] { "Arrow", "Branch", "Cedar", "Dart", "Echo" };
        var colorNames = new[] { "ivory", "red", "blue", "green", "black", "purple" };
        var colorSingular = new[] { "an ivory", "a red", "a blue", "a green", "a black", "a purple" };
        var colorPlural = new[] { "ivories", "reds", "blues", "greens", "blacks", "purples" };
        var ordinalName = new[] { "no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
        var game = 0;
        var rand = new Random();
        var maxBonuses = 0;
        var writer = TextWriter.Null;

        while (game < 2000)
        {
            var players = 2; //rand.Next(2, 6);
            writer.WriteLine($"\nGame {++game:G0}");
            var wildcardHappened = false;
            var returnHappened = false;
            var extraChipsHappened = false;
            var tieHappened = false;
            var bank = Enumerable.Range(0, colors).Select(_ => bankInit[players]).ToArray();
            var chips = Enumerable.Range(0, players).Select(_ => new int[colors]).ToArray();
            var cards = Enumerable.Range(0, players).Select(_ => new List<Card>()).ToArray();
            var score = new int[players];
            var target = new Card?[players];

            var draw = new List<Card>(deck); //.Where(c => players < 5 || c.Points < 5));
            var cardsPerRow = players < 5 ? 5 : 7;
            var cardRows = players < 5 ? 3 : 3;
            var tableau = NewDeal(draw, rand, cardRows, cardsPerRow);

            const int chipsPerTurn = 3;
            const int winningScore = 25;

            var turn = 1;
            var player = 0;
            var buyTurn = 0;

            using (var html = File.CreateText("gen/example.html"))
            {
                var th = 150;
                var tw = (cutWidth * th) / cutHeight;
                var ppm = (pixelsPerMillimeter * th) / cutHeight;
                var csize = (int)(39 * ppm);
                var pleft = (int)(tw * 6 * 1.1 + csize * 1.3);
                const int playerSpacing = 200;
                html.WriteLine("<!DOCTYPE html>");
                html.WriteLine("<html>");
                html.WriteLine("<head>");
                html.WriteLine("<style>");
                html.WriteLine("@import url('https://fonts.googleapis.com/css2?family=Josefin+Sans&family=Saira+Stencil+One&family=Tilt+Warp&display=swap');");
                html.WriteLine("body { margin: 2em; }");
                html.WriteLine($".cards {{ height: {th * 3.4}px; width: {5 * tw * 1.1}px; }}");
                html.WriteLine($".cards img {{ width: {tw}px; height: {th}px; position: absolute; filter: drop-shadow(3px 3px 3px #888888); }}");
                html.WriteLine($".chips {{ height: {csize * 1.3}px; width: {csize * 1.3}px; }}");
                html.WriteLine($".chips img {{ width: {csize}px; height: {csize}px; position: absolute; }}");
                html.WriteLine(".players { padding-left: 1em; }");
                html.WriteLine(".playerHeader { display: flex; justify-content: space-between; }");
                html.WriteLine(".name { font-family: 'Tilt Warp', cursive; font-size: 16pt; }");
                html.WriteLine(".portrait { width: 50px; height: 50px; position: relative; top: 10px; }");
                html.WriteLine($".player {{ display: flex; background-color: #9AC; width: 450px; height: {playerSpacing - 20}px; }}");
                html.WriteLine(".score { margin: 1em; font-family: 'Saira Stencil One', cursive; font-size: 16px; width: 30px; height: 30px; border: 3px solid black; border-radius: 20px; display: flex; justify-content: center; align-items: center; }");
                html.WriteLine(".bonus { margin: .4em 1.35em; font-family: 'Saira Stencil One', cursive; font-size: 14px; width: 25px; height: 25px; border: 3px solid black; border-radius: 20px; display: flex; justify-content: center; align-items: center; background-color: #9EF; }");
                html.WriteLine(".playArea { width: 400px; }");
                html.WriteLine("#message { font-family: 'Josefin Sans', sans-serif; font-size: 16pt; max-width: 700px; line-height: 150%; }");
                html.WriteLine("button { margin: 1.1em; color: #CCC; background-color: #229; border-radius: 0.5em; height: 2em; font-family: 'Josefin Sans', sans-serif; font-size: 16pt; }");
                html.WriteLine("button:disabled { margin: 1.1em; color: #444; background-color: #999; border-radius: 0.5em; height: 2em; font-family: 'Josefin Sans', sans-serif; font-size: 16pt; }");
                html.WriteLine(".flex { display: flex; }");
                html.WriteLine("</style>");
                html.WriteLine("</head>");
                html.WriteLine("<body>");
                html.WriteLine("<div class='flex'>");
                html.WriteLine("<div class='cards'>");

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
                    html.WriteLine($"<img id='card{card.Id}' src='../thumb/card{card.Id}.png' style='left: {x:F0}px; top: {y:F0}px; transform: rotate({card.Angle:F0}deg);'>");
                }

                foreach (var card in draw)
                {
                    html.WriteLine($"<img id='card{card.Id}' src='../thumb/card{card.Id}.png' style='left: -200px; top: 200px; transform: rotate({card.Angle:F0}deg);'>");
                }

                html.WriteLine("</div>");
                html.WriteLine("<div class='chips'>");
                var chipIds = Enumerable.Range(0, colors).Select(_ => new Stack<string>()).ToArray();
                var playerChipIds = Enumerable.Range(0, players).Select(_ => Enumerable.Range(0, colors).Select(_ => new Stack<string>()).ToArray()).ToArray();

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

                html.WriteLine("</div>");
                html.WriteLine("<div class='players'>");

                for (var p = 0; p < players; p++)
                {
                    html.WriteLine("<div class='playerHeader'>");
                    html.WriteLine($"<p class='name'>{names[p]}</p>");
                    html.WriteLine($"<img class='portrait' src='character{p}.png'>");
                    html.WriteLine("</div>");
                    html.WriteLine($"<div class='player'>");
                    html.WriteLine("<div>");
                    html.WriteLine($"<div id='score{p}' class='score'>0</div>");
                    html.WriteLine($"<div id='bonus{p}' class='bonuses'></div>");
                    html.WriteLine("</div>");
                    html.WriteLine($"<div id='player{p}' class='playArea'></div>");
                    html.WriteLine("</div>");
                }

                html.WriteLine("</div>");
                html.WriteLine("</div>");
                html.WriteLine("<div class='flex'>");
                html.WriteLine("<p id='message'></p>");
                html.WriteLine("<button id='advance' type='button' onclick='turn1_0()'>Start game</button>");
                html.WriteLine("</div>");
                html.WriteLine("<script>");
                html.WriteLine("var message = document.getElementById('message');");
                html.WriteLine("var advance = document.getElementById('advance');");
                html.WriteLine("var thingsMoving = [];");
                html.WriteLine("var playerX = [];");
                html.WriteLine("var playerY = [];");
                html.WriteLine("function animateThingFrame(timestamp)");
                html.WriteLine("{");
                html.WriteLine("if (thingsMoving.length == 0) return;");
                html.WriteLine("var c = thingsMoving[0];");
                html.WriteLine("if (c.startTime === undefined) c.startTime = timestamp;");
                html.WriteLine("var elapsed = timestamp - c.startTime;");
                html.WriteLine("var t = Math.min(1, elapsed / 200);");
                html.WriteLine("if (c.startScale === undefined) c.startScale = c.chip.getBoundingClientRect().width / c.chip.offsetWidth;");
                html.WriteLine("c.chip.style.left = (c.startX + t * (c.endX - c.startX)) + 'px';");
                html.WriteLine("c.chip.style.top = (c.startY + t * (c.endY - c.startY)) + 'px';");
                html.WriteLine("c.chip.style.transform = 'scale(' + (c.startScale + t * (c.endScale - c.startScale)) + ')';");
                html.WriteLine("if (t >= 1) thingsMoving.shift();");
                html.WriteLine("if (thingsMoving.length > 0) window.requestAnimationFrame(animateThingFrame);");
                html.WriteLine("}");
                html.WriteLine("function animateThing(chipId, x, y, scale, z)");
                html.WriteLine("{");
                html.WriteLine("var chip = document.getElementById(chipId);");
                html.WriteLine("chip.style.zIndex = z;");
                html.WriteLine("var rect = chip.getBoundingClientRect()");
                html.WriteLine("thingsMoving.push({chip: chip, startX: rect.left, startY: rect.top, endX: x, endY: y, endScale: scale});");
                html.WriteLine("window.requestAnimationFrame(animateThingFrame);");
                html.WriteLine("}");

                while (player > 0 || score.All(s => s < winningScore))
                {
                    if (player == 0)
                    {
                        if (turn > buyTurn + 100)
                        {
                            DumpTableau(tableau, rand, cardRows, "gen/debug-tableau.png");
                            Debugger.Break();
                        }

                        writer.WriteLine($"\nTurn {turn}");
                    }

                    Card? targetCard = null;
                    var best = double.MaxValue;
                    int[] chipsNeeded;
                    html.WriteLine($"function turn{turn}_{player}() {{");

                    if (player == 0 && turn == 1)
                    {
                        for (var p = 0; p < players; p++)
                        {
                            html.WriteLine($"var rect{p} = document.getElementById('player{p}').getBoundingClientRect();");
                            html.WriteLine($"playerX[{p}] = rect{p}.left;");
                            html.WriteLine($"playerY[{p}] = rect{p}.top;");
                        }
                    }

                    html.WriteLine("advance.disabled = true;");
                    var message = new StringBuilder();

                    foreach (var card in tableau.Where(c => c is not null).Cast<Card>().OrderByDescending(c => c.Points))
                    {
                        if (player == players - 1 && card.Points == 0)
                            continue;

                        if (Enumerable.Range(0, players).Where(p => p != player).Select(p => target[p]).Any(c => c?.Id == card.Id))
                            continue;

                        chipsNeeded = card.Cost.Select((c, i) => Math.Max(0, c - chips[player][i] - cards[player].Count(card => card.Color == i))).ToArray();
                        var canBuy = Enumerable.Range(0, colors).All(c => bank[c] >= chipsNeeded[c]);

                        if (!canBuy)
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

                    chipsNeeded = targetCard.Cost.Select((c, i) => Math.Max(0, c - chips[player][i] - cards[player].Count(card => card.Color == i))).ToArray();
                    var excess = Enumerable.Range(0, colors).Select(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Count(k => k.Color == c)))).ToArray();
                    var chipCounts = new int[colors];
                    List<string> chipStr;

                    int GetPlayX(int color) =>
                        color * (int)(tw * 1.1 * .5);

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
                                            html.WriteLine($"animateThing('{chipId}', {x}, {y}, 1, {bank[i]});");
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
                            chipStr = chipCounts.Select((c, i) => c == 0 ? "" : c == 1 ? colorSingular[i] : ordinalName[c] + " " + colorPlural[i]).Where(s => s.Length > 0).ToList();
                            if (chipStr.Count > 1) chipStr[^1] = "and " + chipStr[^1];
                            message.Append(string.Join(chipStr.Count > 2 ? ", " : " ", chipStr));
                            message.Append(" to use as ");
                            message.Append(wild == 1 ? "a wildcard" : ordinalName[wild] + " wildcards");
                            message.Append(" and");
                        }

                        buyTurn = turn;
                        writer.Write($" buys {targetCard.Name}");
                        Array.Clear(chipCounts);

                        for (var c = 0; c < colors; c++)
                        {
                            var pay = Math.Max(0, targetCard.Cost[c] - cards[player].Count(card => card.Color == c));

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
                                html.WriteLine($"animateThing('{chipId}', {x}, {y}, 1, {bank[c]});");
                                ++bank[c];
                            }
                        }

                        message.Append(" invests ");
                        chipStr = chipCounts.Select((c, i) => c == 0 ? "" : c == 1 ? colorSingular[i] : ordinalName[c] + " " + colorPlural[i]).Where(s => s.Length > 0).ToList();
                        if (chipStr.Count > 1) chipStr[^1] = "and " + chipStr[^1];
                        message.Append(chipStr.Count == 0 ? (noChips ? "no chips" : "no additional chips") : string.Join(chipStr.Count > 2 ? ", " : " ", chipStr));
                        message.Append(" to attract ");
                        message.Append(targetCard.Name);
                        message.Append($" to {names[player]}ville");
                        
                        cards[player].Add(targetCard.Stamped());
                        target[player] = null;
                        var z = cards[player].Count(c => c.Color == targetCard.Color);
                        x = GetPlayX(targetCard.Color) - tw / 4;
                        y = 12 * z - th / 4;
                        html.WriteLine($"animateThing('card{targetCard.Id}', {x} + playerX[{player}], {y} + playerY[{player}], .5, {z});");

                        if (targetCard.Points > 0)
                        {
                            score[player] += targetCard.Points;
                            writer.Write($" and scores {targetCard.Points}");

                            if (noChips && targetCard.Bonus > 0)
                            {
                                score[player] += targetCard.Bonus;

                                if (targetCard.Bonus > 0)
                                {
                                    writer.Write($" with +{targetCard.Bonus} bonus");
                                    message.Append($" and earns {ordinalName[targetCard.Bonus]} bonus points");
                                    html.WriteLine($"document.getElementById('bonus{player}').innerHTML += '<div class=bonus>+{targetCard.Bonus}</div>';");
                                }
                            }

                            writer.WriteLine($" for a total of {score[player]}");
                            html.WriteLine($"document.getElementById('score{player}').innerHTML = '{score[player]}';");
                        }

                        message.Append(".");
                        var index = Enumerable.Range(0, tableau.Length).Single(i => tableau[i]?.Id == targetCard.Id);

                        if (draw.Any())
                        {
                            tableau[index] = Draw(draw, rand);
                            writer.WriteLine($"Card is replaced with {tableau[index].Name}");
                            message.Append($" They then draw {tableau[index].Name}.");
                            GetTableauXY(index, out x, out y);
                            html.WriteLine($"animateThing('card{tableau[index].Id}', {x}, {y}, 1, 0);");
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
                        const int chipLimit = 10;

                        for (var i = 0; i < chipsPerTurn; i++)
                        {
                            excess = Enumerable.Range(0, colors).Select(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Count(k => k.Color == c)))).ToArray();

                            if (chips[player].Sum() >= chipLimit + excess.Sum())
                                break;

                            var choices = Enumerable.Range(0, colors).Where(i => !picks.Contains(i) && bank[i] > 0).ToList();

                            if (!choices.Any())
                                break;

                            var j = -1;
                            var max = choices.Max(i => chipsNeeded[i]);

                            if (max > 0)
                            {
                                j = Enumerable.Range(0, colors).ToList().FindIndex(c => bank[c] > 0 && chipsNeeded[c] == max && !picks.Contains(c));
                            }

                            if (j == -1)
                            {
                                j = Enumerable.Range(0, colors).ToList().FindIndex(c => bank[c] > 0 && chipsNeeded[c] > 0 && !picks.Contains(c));
                            }

                            if (j == -1)
                            {
                                if (chips[player].Sum() >= chipLimit)
                                    break;

                                var cc = Enumerable.Range(0, colors).ToList().Scramble(rand);
                                j = cc.First(c => !picks.Contains(c) && bank[c] > 0);
                                extraChips[j]++;
                                extraChipsHappened = true;
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
                            html.WriteLine($"animateThing('{chipId}', {x} + playerX[{player}], {y} + playerY[{player}], .5, {chips[player][j]});");
                            chips[player][j]++;
                        }

                        message.Append($"{names[player]} takes ");
                        chipStr = chipCounts.Select((c, i) => c == 0 ? "" : c == 1 ? colorSingular[i] : ordinalName[c] + " " + colorPlural[i]).Where(s => s.Length > 0).ToList();
                        if (chipStr.Count > 1) chipStr[^1] = "and " + chipStr[^1];
                        message.Append(chipStr.Count == 0 ? "no chips" : string.Join(chipStr.Count > 2 ? ", " : " ", chipStr));
                        message.Append($", intending to attract {targetCard.Name}");

                        if (extraChips.Sum() > 0)
                        {
                            chipStr = extraChips.Select((c, i) => c == 0 ? "" : c == 1 ? colorSingular[i] : ordinalName[c] + " " + colorPlural[i]).Where(s => s.Length > 0).ToList();
                            if (chipStr.Count > 1) chipStr[^1] = "and " + chipStr[^1];
                            message.Append(", and ");
                            message.Append(string.Join(chipStr.Count > 2 ? ", " : " ", chipStr));
                            message.Append(" for later");
                        }

                        if (chips[player].Sum() > chipLimit)
                        {
                            writer.Write(" and returns");
                            returnHappened = true;
                            Array.Clear(chipCounts);

                            while (chips[player].Sum() > chipLimit)
                            {
                                excess = Enumerable.Range(0, colors).Select(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Count(k => k.Color == c)))).ToArray();

                                if (excess.Sum() == 0)
                                    excess = (int[])chips[player].Clone();

                                var extra = Enumerable.Range(0, colors).Where(c => excess[c] > 0).ToList().Scramble(rand).First();
                                writer.Write($" {colorNames[extra]}");

                                if (chips[player][extra] == 0)
                                    Debugger.Break();

                                var chipId = playerChipIds[player][extra].Pop();
                                chipIds[extra].Push(chipId);
                                GetBankXY(extra, out var x, out var y);
                                html.WriteLine($"animateThing('{chipId}', {x}, {y}, 1, {bank[extra]});");
                                chips[player][extra]--;
                                bank[extra]++;
                                chipCounts[extra]++;
                            }

                            message.Append($". They have more than {chipLimit} chips and decide to return ");
                            chipStr = chipCounts.Select((c, i) => c == 0 ? "" : c == 1 ? colorSingular[i] : ordinalName[c] + " " + colorPlural[i]).Where(s => s.Length > 0).ToList();
                            if (chipStr.Count > 1) chipStr[^1] = "and " + chipStr[^1];
                            message.Append(string.Join(chipStr.Count > 2 ? ", " : " ", chipStr));
                        }

                        writer.WriteLine();
                        message.Append(".");
                    }

                    player = (player + 1) % players;
                    var gameOver = player == 0 && score.Any(s => s >= winningScore);

                    if (gameOver)
                    {
                        var winner = Enumerable.Range(0, players).MaxBy(p => score[p]);
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
                        tieHappened = score.Count(s => s == score.Max()) > 1;
                    }
                    else
                    {
                        if (player == 1 && turn == 1)
                        {
                            html.WriteLine($"advance.innerHTML = 'Next';");
                        }

                        html.WriteLine($"advance.onclick = turn{turn}_{player};");
                        html.WriteLine("advance.disabled = false;");
                    }

                    html.WriteLine("}"); // end of script for player turn
                }

                html.WriteLine("</script>");
                html.WriteLine("</body>");
                html.WriteLine("</html>");
            }

            var bonuses = Enumerable.Range(0, players).Sum(p => score[p] - cards[p].Sum(c => c.Points));
            maxBonuses = Math.Max(maxBonuses, bonuses);

            if (bonuses > 0 && players == 2 && wildcardHappened && returnHappened && extraChipsHappened && !tieHappened && turn < 40)
                break;
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