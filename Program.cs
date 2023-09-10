#define COST_SHIFT
#define xGAME_CRAFTER

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
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

        var gammaMap = ColorMap.FromGamma(1.0);

        SKPaint GetFont(string family, SKFontStyle style, SKTextAlign align, float points) =>
            new SKPaint { Color = SKColors.Black, IsAntialias = true, Typeface = SKTypeface.FromFamilyName(family, style), TextAlign = align, TextSize = points * (float)pixelsPerPoint };

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
                using (var canvas = new SKCanvas(skbmp))
                {
                    var sector = sectors[card.Color];
                    var color = colorScheme[card.Color];

                    // Card background image.
                    canvas.DrawBitmap(insetImage[sector], new SKRect(0, 0, cardWidth, cardHeight), smooth);

                    // Card color overlay tint.
                    canvas.DrawRect(new SKRect(0, 0, cardWidth, cardHeight), cardPaint[card.Color]);

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
        DumpTableau(NewDeal(draw, rand, 5), rand, "gen/tableau.png");
        ExampleGame(deck, sectors);
    }

    Card?[] NewDeal(List<Card> draw, Random rand, int perRow)
    {
        var cards = new Card[3 * perRow];

        for (var i = 0; i < 2 * perRow; i++)
        {
            cards[i] = Draw(draw, rand);
        }

        for (var i = 2 * perRow; i < 3 * perRow; i++)
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

    void DumpTableau(Card?[] tableau, Random rand, string filename)
    {
        var thScale = 0.25;
        var gridY = 3;
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
        var game = 0;
        var rand = new Random();
        var maxBonuses = 0;
        var writer = Console.Out;

        while (game < 2000)
        {
            var players = rand.Next(2, 6);
            writer.WriteLine($"\nGame {++game:G0}");
            var wildcardHappened = false;
            var returnHappened = false;
            var bank = Enumerable.Range(0, colors).Select(_ => bankInit[players]).ToArray();
            var chips = Enumerable.Range(0, players).Select(_ => new int[colors]).ToArray();
            var cards = Enumerable.Range(0, players).Select(_ => new List<Card>()).ToArray();
            var score = new int[players];
            var target = new Card?[players];

            var draw = new List<Card>(deck.Where(c => players < 5 || c.Points < 5));
            const int cardsPerRow = 5;
            var tableau = NewDeal(draw, rand, cardsPerRow);

            const int chipsPerTurn = 3;
            const int winningScore = 25;

            var turn = 0;
            var player = 0;
            var buyTurn = 0;

            while (player > 0 || score.All(s => s < winningScore))
            {
                if (player == 0)
                {
                    if (turn > buyTurn + 100)
                    {
                        DumpTableau(tableau, rand, "gen/debug-tableau.png");
                        Debugger.Break();
                    }

                    using (var html = File.CreateText($"gen/example{turn}.html"))
                    {
                        html.WriteLine("<!DOCTYPE html>");
                        html.WriteLine("<html>");
                        html.WriteLine("<body>");
                        var th = 150;
                        var tw = (cutWidth * th) / cutHeight;
                        var ppm = (pixelsPerMillimeter * th) / cutHeight;

                        for (var i = 0; i < tableau.Length; i++)
                        {
                            var card = tableau[i];

                            if (card == null)
                                continue;

                            var gridX = i % cardsPerRow;
                            var gridY = i / cardsPerRow;
                            var x = gridX * tw * 1.1;
                            var y = gridY * (th + tw * .1);
                            html.WriteLine($"<img src=\"../thumb/card{card.Id}.png\" width=\"{tw}\" height=\"{th}\" style=\"position: absolute; left: {x:F0}px; top: {y:F0}px; transform: rotate({card.Angle:F0}deg); filter: drop-shadow(3px 3px 3px black);\">");
                        }

                        var csize = (int)(39 * ppm);

                        for (var c = 0; c < colors; c++)
                        {
                            for (var j = 0; j < bank[c]; j++)
                            {
                                var x = (c * csize * 3) / 2 + 3 * j;
                                var y = 3 * (th + tw * .1) + 5 * j;
                                html.WriteLine($"<img src=\"{sectors[c]}-disc.png\" width=\"{csize}\" height=\"{csize}\" style=\"position: absolute; left: {x:F0}px; top: {y:F0}px;\">");
                            }
                        }

                        for (var p = 0; p < players; p++)
                        {
                            html.WriteLine("<div>");
                            html.WriteLine($"<h1>{names[p]}</h1>");
                            var groupIndex = 0;
                            
                            foreach (var group in cards[player].GroupBy(c => c.Color))
                            {
                                foreach (var card in group.OrderBy(c => c.Acquision))
                                {
                                    
                                }

                                groupIndex++;
                            }

                            html.WriteLine("</div>");
                        }

                        html.WriteLine("</body>");
                        html.WriteLine("</html>");
                    }

                    ++turn;
                    writer.WriteLine($"\nTurn {turn}");
                }

                Card? targetCard = null;
                var best = double.MaxValue;
                int[] chipsNeeded;

                foreach (var card in tableau.Where(c => c is not null).Cast<Card>().OrderByDescending(c => c.Points))
                {
                    if (player == 2 && card.Points == 0)
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

                if (chipsNeeded.Sum() <= excess.Sum() / 3)
                {
                    writer.Write($"{names[player]}");
                    var wild = 0;

                    if (chipsNeeded.Sum() > 0)
                    {
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
                                        excess[i]--;
                                        chips[player][i]--;
                                        bank[i]++;
                                        writer.Write($" {colorNames[i]}");
                                        break;
                                    }
                                }
                            }

                            wild++;
                        }

                        writer.Write(" for wildcard and");
                        wildcardHappened = true;
                    }

                    buyTurn = turn;
                    writer.Write($" buys {targetCard.Name}");
                    var noChips = true;

                    for (var c = 0; c < colors; c++)
                    {
                        var pay = Math.Max(0, targetCard.Cost[c] - cards[player].Count(card => card.Color == c));

                        if (pay > 0)
                            noChips = false;

                        while (pay > chips[player][c] && wild > 0)
                        {
                            chips[player][c]++;
                            bank[c]--;
                            wild--;
                        }

                        if (pay > chips[player][c])
                            Debugger.Break();

                        chips[player][c] -= pay;
                        bank[c] += pay;
                    }

                    cards[player].Add(targetCard.Stamped());
                    target[player] = null;
                    score[player] += targetCard.Points;

                    if (targetCard.Points > 0)
                        writer.Write($" and scores {targetCard.Points}");

                    if (noChips && targetCard.Bonus > 0)
                    {
                        score[player] += targetCard.Bonus;

                        if (targetCard.Bonus > 0)
                            writer.Write($" with +{targetCard.Bonus} bonus");
                    }

                    writer.WriteLine($" for a total of {score[player]}");
                    var index = Enumerable.Range(0, tableau.Length).Single(i => tableau[i]?.Id == targetCard.Id);

                    if (draw.Any())
                    {
                        tableau[index] = Draw(draw, rand);
                        writer.WriteLine($"Card is replaced with {tableau[index].Name}");
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
                    var wasNeeded = (int[])chipsNeeded.Clone();

                    for (var i = 0; i < chipsPerTurn; i++)
                    {
                        excess = Enumerable.Range(0, colors).Select(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Count(k => k.Color == c)))).ToArray();

                        if (chips[player].Sum() >= 10 + excess.Sum())
                            break;

                        var choices = Enumerable.Range(0, colors).Where(i => !picks.Contains(i) && bank[i] > 0).ToList();

                        if (!choices.Any())
                            break;

                        var max = choices.Max(i => chipsNeeded[i]);
                        var j = Enumerable.Range(0, colors).ToList().FindIndex(c => bank[c] > 0 && chipsNeeded[c] == max && !picks.Contains(c));

                        if (j == -1)
                        {
                            j = Enumerable.Range(0, colors).ToList().FindIndex(c => bank[c] > 0 && chipsNeeded[c] > 0 && !picks.Contains(c));
                        }

                        if (j == -1)
                        {
                            if (chips[player].Sum() >= 10)
                                break;

                            var cc = Enumerable.Range(0, colors).ToList().Scramble(rand);
                            j = cc.First(c => !picks.Contains(c) && bank[c] > 0);
                        }

                        picks.Add(j);
                        if (chipsNeeded[j] > 0) chipsNeeded[j]--;

                        if (bank[j] == 0)
                            Debugger.Break();

                        bank[j]--;
                        chips[player][j]++;
                        writer.Write($" {colorNames[j]}");
                    }

                    if (chips[player].Sum() > 10)
                    {
                        writer.Write(" and returns");
                        returnHappened = true;

                        while (chips[player].Sum() > 10)
                        {
                            excess = Enumerable.Range(0, colors).Select(c => Math.Max(0, chips[player][c] - Math.Max(0, targetCard.Cost[c] - cards[player].Count(k => k.Color == c)))).ToArray();

                            if (excess.Sum() == 0)
                                excess = (int[])chips[player].Clone();

                            var extra = Enumerable.Range(0, colors).Where(c => excess[c] > 0).ToList().Scramble(rand).First();
                            writer.Write($" {colorNames[extra]}");

                            if (chips[player][extra] == 0)
                                Debugger.Break();

                            chips[player][extra]--;
                            bank[extra]++;
                        }
                    }

                    writer.WriteLine();
                }

                player = (player + 1) % players;

                if (player == 0)
                {
                    writer.Write("Bank has");

                    for (var c = 0; c < colors; c++)
                      writer.Write($" {bank[c]} {colorNames[c]},");

                    writer.WriteLine();
                }
            }

            var bonuses = Enumerable.Range(0, players).Sum(p => score[p] - cards[p].Sum(c => c.Points));
            maxBonuses = Math.Max(maxBonuses, bonuses);

            if (bonuses > 0 && players == 3 && wildcardHappened && returnHappened)
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