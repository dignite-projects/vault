using System;
using System.Collections.Generic;
using System.Linq;
using UglyToad.PdfPig.Core;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// Detects the drawn ruling-line grids on a page (#450 lattice path) from its vector subpath bounding
/// rectangles. When a table draws its column/row separators as lines — bank statements, forms, financial
/// reports — those lines are the <b>authoritative</b> column/row model: deterministic, unlike the whitespace
/// heuristics of <see cref="PdfTableReconstruction"/>. Returns one <see cref="Grid"/> per drawn table; a page
/// with no ruled grid (borderless tables, or a table with only row rules and no column separators) yields an
/// empty list and the caller keeps the whitespace / stream path.
/// <para>
/// Robust to the two common encodings and their quirks: a ruling can be a thin filled rectangle OR a stroked
/// line; a table border can be one stroked rectangle (its four sides); and rulings are frequently
/// <b>double-stroked</b> (two near-coincident segments ~a point apart). Crucially, rules are grouped into
/// SEPARATE tables by intersection (a connected component of mutually-crossing rules), so two distinct tables
/// on one page are not merged into a single spurious grid.
/// </para>
/// </summary>
internal static class PdfRulingLines
{
    /// <summary>
    /// One drawn table grid: the ascending x of the vertical rules (column separators) and ascending y of the
    /// horizontal rules (row separators). N boundaries delimit N-1 cells along that axis.
    /// <para>
    /// <see cref="Horizontals"/> and <see cref="Verticals"/> are the drawn rules themselves. A boundary that is not drawn
    /// between two cells merges them (a cell spanning rows or columns); without the rules every boundary counts as drawn.
    /// </para>
    /// </summary>
    public readonly record struct Grid(IReadOnlyList<double> ColumnBoundaries, IReadOnlyList<double> RowBoundaries)
    {
        public int ColumnCount => ColumnBoundaries.Count - 1;

        public int RowCount => RowBoundaries.Count - 1;

        public IReadOnlyList<HRule> Horizontals { get; init; } = Array.Empty<HRule>();

        public IReadOnlyList<VRule> Verticals { get; init; } = Array.Empty<VRule>();

        /// <summary>
        /// Whether the vertical boundary between columns <paramref name="column"/> and <paramref name="column"/> + 1 is
        /// drawn along most of the row band <paramref name="bandFromBottom"/> (counted from the bottom, as the rows are).
        /// </summary>
        public bool HasVerticalEdge(int column, int bandFromBottom, double tolerance)
        {
            if (Verticals.Count == 0)
            {
                return true;
            }

            var x = ColumnBoundaries[column + 1];
            var low = RowBoundaries[bandFromBottom];
            var high = RowBoundaries[bandFromBottom + 1];
            return Covered(
                Verticals.Where(rule => Math.Abs(rule.X - x) <= tolerance).Select(rule => (rule.Y1, rule.Y2)),
                low,
                high);
        }

        /// <summary>
        /// Whether the horizontal boundary above row band <paramref name="bandFromBottom"/> (between it and the band above)
        /// is drawn along most of column <paramref name="column"/>.
        /// </summary>
        public bool HasHorizontalEdgeAbove(int column, int bandFromBottom, double tolerance)
        {
            if (Horizontals.Count == 0)
            {
                return true;
            }

            var y = RowBoundaries[bandFromBottom + 1];
            var low = ColumnBoundaries[column];
            var high = ColumnBoundaries[column + 1];
            return Covered(
                Horizontals.Where(rule => Math.Abs(rule.Y - y) <= tolerance).Select(rule => (rule.X1, rule.X2)),
                low,
                high);
        }

        /// <summary>Whether the segments cover at least half of [<paramref name="low"/>, <paramref name="high"/>].</summary>
        private static bool Covered(IEnumerable<(double From, double To)> segments, double low, double high)
        {
            var covered = 0.0;
            var reach = low;
            foreach (var (from, to) in segments.OrderBy(segment => segment.From))
            {
                var start = Math.Max(from, reach);
                var end = Math.Min(to, high);
                if (end > start)
                {
                    covered += end - start;
                    reach = end;
                }
            }

            return covered >= (high - low) * 0.5;
        }
    }

    /// <summary>A drawn horizontal rule at <see cref="Y"/> from <see cref="X1"/> to <see cref="X2"/>.</summary>
    public readonly record struct HRule(double Y, double X1, double X2);

    /// <summary>A drawn vertical rule at <see cref="X"/> from <see cref="Y1"/> to <see cref="Y2"/>.</summary>
    public readonly record struct VRule(double X, double Y1, double Y2);

    /// <summary>
    /// Detects every drawn table grid on the page (top-to-bottom), or an empty list when none has at least a
    /// 2×2 cell structure. <paramref name="minRuleLength"/> filters tick marks / glyph strokes,
    /// <paramref name="maxRuleThickness"/> separates a thin rule from a filled box, and
    /// <paramref name="clusterTolerance"/> collapses double-stroked / near-coincident rules and sets the
    /// intersection slack.
    /// </summary>
    public static IReadOnlyList<Grid> DetectGrids(
        IReadOnlyList<PdfRectangle> subpathBounds,
        double minRuleLength = 18.0,
        double maxRuleThickness = 3.0,
        double clusterTolerance = 3.0)
    {
        var horizontals = new List<HRule>();
        var verticals = new List<VRule>();
        // Rectangles that matched none of the three shapes below — a report engine that draws its grid as many
        // small per-row / per-cell boxes (rather than long continuous lines) leaves both its row separators
        // (wide, but only one row tall — too thick for a thin rule, too short for a bordered box) and its column
        // dividers (thin, but individually shorter than minRuleLength because each is redrawn per row) here.
        // Chained below instead of being silently dropped.
        var leftovers = new List<PdfRectangle>();

        foreach (var rect in subpathBounds)
        {
            var left = Math.Min(rect.Left, rect.Right);
            var right = Math.Max(rect.Left, rect.Right);
            var bottom = Math.Min(rect.Bottom, rect.Top);
            var top = Math.Max(rect.Bottom, rect.Top);
            var width = right - left;
            var height = top - bottom;

            if (width >= minRuleLength && height <= maxRuleThickness)
            {
                horizontals.Add(new HRule((top + bottom) / 2.0, left, right));
            }
            else if (height >= minRuleLength && width <= maxRuleThickness)
            {
                verticals.Add(new VRule((left + right) / 2.0, bottom, top));
            }
            else if (width >= minRuleLength && height >= minRuleLength)
            {
                // A large rectangle (a stroked table/cell border, or a shaded fill): its four sides are rules.
                horizontals.Add(new HRule(top, left, right));
                horizontals.Add(new HRule(bottom, left, right));
                verticals.Add(new VRule(left, bottom, top));
                verticals.Add(new VRule(right, bottom, top));
            }
            else
            {
                leftovers.Add(rect);
            }
        }

        ChainStackedRowBoxes(leftovers, minRuleLength, clusterTolerance, horizontals);
        ChainCollinearVerticalSegments(leftovers, maxRuleThickness, minRuleLength, clusterTolerance, verticals);

        // Group rules into separate tables: a table is a connected component of rules where a vertical and a
        // horizontal cross. Union-find over [0..h) horizontals and [h..h+v) verticals.
        var h = horizontals.Count;
        var v = verticals.Count;
        var parent = new int[h + v];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (var i = 0; i < h; i++)
        {
            for (var j = 0; j < v; j++)
            {
                if (Crosses(horizontals[i], verticals[j], clusterTolerance))
                {
                    parent[Find(i)] = Find(h + j);
                }
            }
        }

        var rowsByComponent = new Dictionary<int, List<double>>();
        var columnsByComponent = new Dictionary<int, List<double>>();
        var horizontalsByComponent = new Dictionary<int, List<HRule>>();
        var verticalsByComponent = new Dictionary<int, List<VRule>>();
        for (var i = 0; i < h; i++)
        {
            AddTo(rowsByComponent, Find(i), horizontals[i].Y);
            AddTo(horizontalsByComponent, Find(i), horizontals[i]);
        }

        for (var j = 0; j < v; j++)
        {
            AddTo(columnsByComponent, Find(h + j), verticals[j].X);
            AddTo(verticalsByComponent, Find(h + j), verticals[j]);
        }

        var grids = new List<Grid>();
        foreach (var (component, columnPositions) in columnsByComponent)
        {
            if (!rowsByComponent.TryGetValue(component, out var rowPositions))
            {
                continue; // vertical rules with no crossing horizontals — not a grid
            }

            var columns = Cluster(columnPositions, clusterTolerance);
            var rows = Cluster(rowPositions, clusterTolerance);

            // A lattice needs >= 2 columns AND >= 2 rows (>= 3 boundaries on each axis).
            if (columns.Count >= 3 && rows.Count >= 3)
            {
                grids.Add(new Grid(columns, rows)
                {
                    Horizontals = horizontalsByComponent[component],
                    Verticals = verticalsByComponent[component]
                });
            }
        }

        // Top-to-bottom (by the grid's top edge) for a stable, reading-order-ish sequence.
        return grids.OrderByDescending(g => g.RowBoundaries[^1]).ToList();
    }

    /// <summary>
    /// Rescues a table whose grid is drawn as many small per-row boxes — each row its own rectangle, wide but
    /// only one row tall, so it qualifies as neither a thin rule nor a big bordered box on its own — a common
    /// encoding for report-engine output (bank statements, forms). A maximal vertically-stacked run of >= 2
    /// rectangles sharing the same column span (left/right within <paramref name="clusterTolerance"/>) and
    /// touching top-to-bottom (each one's bottom within tolerance of the next one's top) implicitly draws a
    /// horizontal rule at EVERY member's own top and bottom edge: the boundary between two adjacent members IS
    /// the row separator, regardless of how thin or thick any single box measures. A rectangle with no
    /// vertically-adjacent same-width partner has nothing to confirm it as a table row, so it is left alone
    /// (stays unclaimed, exactly as before this method existed) — a single stray medium-sized shape never
    /// becomes a spurious rule.
    /// </summary>
    private static void ChainStackedRowBoxes(
        IReadOnlyList<PdfRectangle> leftovers, double minRuleLength, double clusterTolerance,
        List<HRule> horizontals)
    {
        var candidates = leftovers
            .Select(r => (
                Left: Math.Min(r.Left, r.Right), Right: Math.Max(r.Left, r.Right),
                Bottom: Math.Min(r.Bottom, r.Top), Top: Math.Max(r.Bottom, r.Top)))
            .Where(r => r.Right - r.Left >= minRuleLength) // wide enough to plausibly span table columns
            .ToList();

        var consumed = new bool[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            if (consumed[i])
            {
                continue;
            }

            // Gather every not-yet-consumed candidate sharing this one's column span (same left/right).
            var group = new List<int> { i };
            consumed[i] = true;
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (!consumed[j]
                    && Math.Abs(candidates[j].Left - candidates[i].Left) <= clusterTolerance
                    && Math.Abs(candidates[j].Right - candidates[i].Right) <= clusterTolerance)
                {
                    group.Add(j);
                    consumed[j] = true;
                }
            }

            if (group.Count < 2)
            {
                continue; // a lone medium box has no partner to confirm it as a grid row.
            }

            // Walk the group top-to-bottom; each maximal touching run of >= 2 boxes contributes every member's
            // own top/bottom edge (at the group's shared column span) as a horizontal rule candidate.
            var ordered = group.Select(idx => candidates[idx]).OrderByDescending(r => r.Top).ToList();
            var runStart = 0;
            for (var k = 1; k <= ordered.Count; k++)
            {
                var chainBreaks = k == ordered.Count || ordered[k - 1].Bottom - ordered[k].Top > clusterTolerance;
                if (!chainBreaks)
                {
                    continue;
                }

                if (k - runStart >= 2)
                {
                    for (var m = runStart; m < k; m++)
                    {
                        horizontals.Add(new HRule(ordered[m].Top, ordered[m].Left, ordered[m].Right));
                        horizontals.Add(new HRule(ordered[m].Bottom, ordered[m].Left, ordered[m].Right));
                    }
                }

                runStart = k;
            }
        }
    }

    /// <summary>
    /// Rescues a table's column dividers when they are redrawn per row (a short vertical segment for every row,
    /// rather than one continuous line for the whole table) — each individual segment is thin enough to be a
    /// vertical rule but shorter than <paramref name="minRuleLength"/> on its own. Groups thin leftover
    /// rectangles by shared centre-x (within <paramref name="clusterTolerance"/>), then within each group merges
    /// any vertically touching/overlapping run into one combined span; a run whose combined length clears
    /// <paramref name="minRuleLength"/> becomes one vertical rule. A segment with no touching same-x partner (or
    /// whose merged run still falls short) stays unclaimed — an isolated tick mark or glyph stroke never becomes
    /// a spurious rule, exactly as before this method existed.
    /// </summary>
    private static void ChainCollinearVerticalSegments(
        IReadOnlyList<PdfRectangle> leftovers, double maxRuleThickness, double minRuleLength,
        double clusterTolerance, List<VRule> verticals)
    {
        var candidates = leftovers
            .Select(r => (
                Left: Math.Min(r.Left, r.Right), Right: Math.Max(r.Left, r.Right),
                Bottom: Math.Min(r.Bottom, r.Top), Top: Math.Max(r.Bottom, r.Top)))
            .Where(r => r.Right - r.Left <= maxRuleThickness) // thin enough to plausibly be a divider
            .ToList();

        var consumed = new bool[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            if (consumed[i])
            {
                continue;
            }

            var centreX = (candidates[i].Left + candidates[i].Right) / 2.0;
            var group = new List<int> { i };
            consumed[i] = true;
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (!consumed[j]
                    && Math.Abs((candidates[j].Left + candidates[j].Right) / 2.0 - centreX) <= clusterTolerance)
                {
                    group.Add(j);
                    consumed[j] = true;
                }
            }

            if (group.Count < 2)
            {
                continue; // a lone short segment has no partner to merge into a longer rule.
            }

            var ordered = group.Select(idx => candidates[idx]).OrderBy(r => r.Bottom).ToList();
            var runStart = 0;
            for (var k = 1; k <= ordered.Count; k++)
            {
                var chainBreaks = k == ordered.Count || ordered[k].Bottom - ordered[k - 1].Top > clusterTolerance;
                if (!chainBreaks)
                {
                    continue;
                }

                var mergedBottom = ordered[runStart].Bottom;
                var mergedTop = ordered[k - 1].Top;
                if (k - runStart >= 2 && mergedTop - mergedBottom >= minRuleLength)
                {
                    verticals.Add(new VRule(centreX, mergedBottom, mergedTop));
                }

                runStart = k;
            }
        }
    }

    /// <summary>Whether a horizontal and a vertical rule cross (within <paramref name="tolerance"/> slack).</summary>
    private static bool Crosses(HRule horizontal, VRule vertical, double tolerance)
        => vertical.X >= horizontal.X1 - tolerance && vertical.X <= horizontal.X2 + tolerance
        && horizontal.Y >= vertical.Y1 - tolerance && horizontal.Y <= vertical.Y2 + tolerance;

    private static void AddTo<T>(Dictionary<int, List<T>> map, int key, T value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<T>();
            map[key] = list;
        }

        list.Add(value);
    }

    /// <summary>
    /// Merges ascending positions that sit within <paramref name="tolerance"/> of the running cluster into one
    /// boundary (the cluster mean), collapsing double-stroked / near-coincident rules. Returns the boundaries
    /// ascending. A real column gutter / row pitch is tens of points, far above the tolerance, so distinct
    /// boundaries never merge.
    /// </summary>
    private static List<double> Cluster(List<double> values, double tolerance)
    {
        var result = new List<double>();
        if (values.Count == 0)
        {
            return result;
        }

        values.Sort();
        var clusterStart = values[0];
        var sum = values[0];
        var count = 1;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] - clusterStart <= tolerance)
            {
                sum += values[i];
                count++;
            }
            else
            {
                result.Add(sum / count);
                clusterStart = values[i];
                sum = values[i];
                count = 1;
            }
        }

        result.Add(sum / count);
        return result;
    }
}
