using System;
using System.Collections.Generic;
using System.Globalization;

namespace CommonUtilities
{
    /// <summary>
    /// A persisted window placement: position in PHYSICAL pixels (matching
    /// Avalonia's <c>PixelPoint</c>), size in DIPs (matching Avalonia's
    /// <c>Width</c>/<c>Height</c>), plus whether the window was maximized.
    /// </summary>
    /// <remarks>
    /// The position/size always describe the <b>normal</b> (restored) rectangle,
    /// even when <see cref="Maximized"/> is true, so un-maximizing lands the window
    /// back in the right place and size.
    /// </remarks>
    public readonly record struct WindowPlacementRecord(int X, int Y, double Width, double Height, bool Maximized)
    {
        /// <summary>
        /// Serializes to a compact, culture-invariant <c>"x;y;w;h;max"</c> string.
        /// No JSON / reflection so it stays Native-AOT friendly.
        /// </summary>
        public string Serialize()
        {
            return string.Join(';', new[]
            {
                X.ToString(CultureInfo.InvariantCulture),
                Y.ToString(CultureInfo.InvariantCulture),
                Width.ToString(CultureInfo.InvariantCulture),
                Height.ToString(CultureInfo.InvariantCulture),
                Maximized ? "1" : "0",
            });
        }

        /// <summary>
        /// Parses a string produced by <see cref="Serialize"/>. Returns false for
        /// null/blank/corrupt input or non-positive dimensions, so a bad value can
        /// never restore a degenerate window.
        /// </summary>
        public static bool TryParse(string? raw, out WindowPlacementRecord record)
        {
            record = default;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var parts = raw.Split(';');
            if (parts.Length < 5)
                return false;

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x))
                return false;
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                return false;
            if (!double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                return false;
            if (!double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                return false;

            if (double.IsNaN(w) || double.IsNaN(h) || double.IsInfinity(w) || double.IsInfinity(h))
                return false;
            if (w < 1 || h < 1)
                return false;

            var maxText = parts[4].Trim();
            bool max = maxText == "1" || maxText.Equals("true", StringComparison.OrdinalIgnoreCase);

            record = new WindowPlacementRecord(x, y, w, h, max);
            return true;
        }
    }

    /// <summary>
    /// Pure, framework-agnostic geometry helpers for keeping a restored window
    /// reachable across monitor changes. All coordinates are PHYSICAL pixels.
    /// Kept free of any Avalonia <c>Window</c> dependency so it is unit-testable.
    /// </summary>
    public static class WindowPlacement
    {
        /// <summary>Minimum on-screen width (px) for the window to count as reachable.</summary>
        public const int MinVisibleWidth = 120;

        /// <summary>
        /// Minimum on-screen height (px) — roughly a title bar, so the user can always
        /// grab and drag the window even if most of it is off-screen.
        /// </summary>
        public const int MinVisibleHeight = 40;

        /// <summary>
        /// True when the window rect (<paramref name="x"/>, <paramref name="y"/>,
        /// <paramref name="w"/>, <paramref name="h"/>) overlaps at least one screen's
        /// working area by <paramref name="minW"/> × <paramref name="minH"/> px — i.e.
        /// a grabbable chunk is on-screen.
        /// </summary>
        public static bool IsVisibleEnough(
            int x, int y, int w, int h,
            IReadOnlyList<(int X, int Y, int W, int H)> screens,
            int minW = MinVisibleWidth, int minH = MinVisibleHeight)
        {
            if (screens == null || w <= 0 || h <= 0)
                return false;

            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                int ix = Math.Max(x, s.X);
                int iy = Math.Max(y, s.Y);
                int ax = Math.Min(x + w, s.X + s.W);
                int ay = Math.Min(y + h, s.Y + s.H);

                if (ax - ix >= minW && ay - iy >= minH)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Top-left (physical px) that centers a <paramref name="winW"/> ×
        /// <paramref name="winH"/> window inside <paramref name="screen"/>'s working
        /// area, never letting the title bar go above/left of that area.
        /// </summary>
        public static (int X, int Y) CenterIn(
            (int X, int Y, int W, int H) screen, int winW, int winH)
        {
            int x = screen.X + (screen.W - winW) / 2;
            int y = screen.Y + (screen.H - winH) / 2;
            return (Math.Max(screen.X, x), Math.Max(screen.Y, y));
        }
    }
}
