using System.Globalization;
using System.Net;
using System.Text;

namespace Telescribe.Core.Site;

/// <summary>Small dependency-free inline SVG charts for the analytics report. Every label is HTML-escaped.</summary>
public static class Charts
{
    private const string Accent = "#3498db";
    private const string Muted = "#7f8c8d";

    /// <summary>Vertical bars, one per item. Labels are thinned out so they never overlap.</summary>
    public static string Bars(IReadOnlyList<(string Label, double Value)> items, string unit)
    {
        if (items.Count == 0)
        {
            return Empty();
        }

        const double width = 720, height = 260, left = 48, bottom = 44, top = 14, right = 10;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var max = Math.Max(1, items.Max(i => i.Value));
        var slot = plotWidth / items.Count;
        var barWidth = Math.Max(2, slot * 0.7);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(items.Count / 12.0));

        var svg = Open(width, height);
        AppendAxis(svg, left, top, plotWidth, plotHeight, max);

        for (var i = 0; i < items.Count; i++)
        {
            var (label, value) = items[i];
            var barHeight = value / max * plotHeight;
            var x = left + i * slot + (slot - barWidth) / 2;
            var y = top + plotHeight - barHeight;

            svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(barWidth)}\" height=\"{N(barHeight)}\" fill=\"{Accent}\" rx=\"2\">")
               .Append($"<title>{Esc(label)}: {Esc(Format(value))} {Esc(unit)}</title></rect>");

            if (i % labelEvery == 0)
            {
                svg.Append($"<text x=\"{N(x + barWidth / 2)}\" y=\"{N(top + plotHeight + 16)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{Muted}\">{Esc(label)}</text>");
            }
        }

        return Close(svg);
    }

    /// <summary>A line with a dot per point, for values over time.</summary>
    public static string Line(IReadOnlyList<(DateTime X, double Y)> points, string unit)
    {
        if (points.Count < 2)
        {
            return Empty();
        }

        const double width = 720, height = 260, left = 56, bottom = 44, top = 14, right = 16;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var minX = points.Min(p => p.X).Ticks;
        var spanX = Math.Max(1, points.Max(p => p.X).Ticks - minX);
        var max = Math.Max(1, points.Max(p => p.Y));

        string Px(DateTime x) => N(left + (x.Ticks - minX) / (double)spanX * plotWidth);
        string Py(double y) => N(top + plotHeight - y / max * plotHeight);

        var svg = Open(width, height);
        AppendAxis(svg, left, top, plotWidth, plotHeight, max);

        svg.Append("<polyline fill=\"none\" stroke=\"").Append(Accent).Append("\" stroke-width=\"2\" points=\"")
           .Append(string.Join(' ', points.Select(p => $"{Px(p.X)},{Py(p.Y)}")))
           .Append("\"/>");

        foreach (var (x, y) in points)
        {
            svg.Append($"<circle cx=\"{Px(x)}\" cy=\"{Py(y)}\" r=\"3\" fill=\"{Accent}\"><title>{Esc(x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}: {Esc(Format(y))} {Esc(unit)}</title></circle>");
        }

        var first = points[0].X;
        var last = points[^1].X;
        svg.Append($"<text x=\"{N(left)}\" y=\"{N(top + plotHeight + 18)}\" font-size=\"11\" fill=\"{Muted}\">{Esc(first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}</text>")
           .Append($"<text x=\"{N(left + plotWidth)}\" y=\"{N(top + plotHeight + 18)}\" text-anchor=\"end\" font-size=\"11\" fill=\"{Muted}\">{Esc(last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}</text>");

        return Close(svg);
    }

    /// <summary>Weekday (rows, Monday first) by hour (columns) grid; darker means more.</summary>
    public static string Heatmap(int[,] counts, string unit)
    {
        string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        var max = 0;
        foreach (var value in counts)
        {
            max = Math.Max(max, value);
        }

        if (max == 0)
        {
            return Empty();
        }

        const double cell = 26, left = 40, top = 8, bottom = 22;
        var svg = Open(left + cell * 24 + 8, top + cell * 7 + bottom);

        for (var day = 0; day < 7; day++)
        {
            svg.Append($"<text x=\"{N(left - 8)}\" y=\"{N(top + day * cell + cell * 0.65)}\" text-anchor=\"end\" font-size=\"11\" fill=\"{Muted}\">{days[day]}</text>");
            for (var hour = 0; hour < 24; hour++)
            {
                var value = counts[day, hour];
                var opacity = value == 0 ? 0.06 : 0.2 + 0.8 * value / max;
                svg.Append($"<rect x=\"{N(left + hour * cell + 1)}\" y=\"{N(top + day * cell + 1)}\" width=\"{N(cell - 2)}\" height=\"{N(cell - 2)}\" rx=\"3\" fill=\"{Accent}\" fill-opacity=\"{N(opacity)}\">")
                   .Append($"<title>{days[day]} {hour:00}:00 UTC: {value} {Esc(unit)}</title></rect>");
            }
        }

        for (var hour = 0; hour < 24; hour += 3)
        {
            svg.Append($"<text x=\"{N(left + hour * cell + cell / 2)}\" y=\"{N(top + cell * 7 + 15)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{Muted}\">{hour:00}</text>");
        }

        return Close(svg);
    }

    /// <summary>Horizontal bars with the label on the left and the value at the end.</summary>
    public static string HorizontalBars(IReadOnlyList<(string Label, double Value)> items)
    {
        if (items.Count == 0)
        {
            return Empty();
        }

        const double width = 720, rowHeight = 28, left = 150, right = 70;
        var max = Math.Max(1, items.Max(i => i.Value));
        var svg = Open(width, items.Count * rowHeight + 8);

        for (var i = 0; i < items.Count; i++)
        {
            var (label, value) = items[i];
            var barWidth = value / max * (width - left - right);
            var y = 4 + i * rowHeight;

            svg.Append($"<text x=\"{N(left - 10)}\" y=\"{N(y + 18)}\" text-anchor=\"end\" font-size=\"14\">{Esc(label)}</text>")
               .Append($"<rect x=\"{N(left)}\" y=\"{N(y + 3)}\" width=\"{N(Math.Max(2, barWidth))}\" height=\"{N(rowHeight - 8)}\" rx=\"3\" fill=\"{Accent}\"><title>{Esc(label)}: {Esc(Format(value))}</title></rect>")
               .Append($"<text x=\"{N(left + Math.Max(2, barWidth) + 8)}\" y=\"{N(y + 18)}\" font-size=\"12\" fill=\"{Muted}\">{Esc(Format(value))}</text>");
        }

        return Close(svg);
    }

    private static void AppendAxis(StringBuilder svg, double left, double top, double plotWidth, double plotHeight, double max)
    {
        for (var tick = 0; tick <= 4; tick++)
        {
            var y = top + plotHeight - plotHeight * tick / 4;
            svg.Append($"<line x1=\"{N(left)}\" y1=\"{N(y)}\" x2=\"{N(left + plotWidth)}\" y2=\"{N(y)}\" stroke=\"#e9ecef\"/>")
               .Append($"<text x=\"{N(left - 6)}\" y=\"{N(y + 4)}\" text-anchor=\"end\" font-size=\"11\" fill=\"{Muted}\">{Esc(Format(max * tick / 4))}</text>");
        }
    }

    private static StringBuilder Open(double width, double height) =>
        new StringBuilder($"<svg viewBox=\"0 0 {N(width)} {N(height)}\" width=\"100%\" role=\"img\" style=\"max-width:{N(width)}px;height:auto\" xmlns=\"http://www.w3.org/2000/svg\">");

    private static string Close(StringBuilder svg) => svg.Append("</svg>").ToString();

    private static string Empty() => "<p class=\"chart-empty\" style=\"color:#7f8c8d\">Not enough data yet.</p>";

    private static string Esc(string text) => WebUtility.HtmlEncode(text);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Format(double value) => value >= 1000
        ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : value.ToString("0.##", CultureInfo.InvariantCulture);
}
