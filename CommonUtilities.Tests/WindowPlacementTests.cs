using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CommonUtilities;
using Xunit;

namespace CommonUtilities.Tests
{
    public class WindowPlacementTests
    {
        // ── WindowPlacementRecord round-trip / parsing ───────────────────────────

        [Fact]
        public void Serialize_Then_TryParse_RoundTrips()
        {
            var original = new WindowPlacementRecord(120, -340, 1400.5, 900.25, true);

            Assert.True(WindowPlacementRecord.TryParse(original.Serialize(), out var parsed));
            Assert.Equal(original, parsed);
        }

        [Fact]
        public void Serialize_NegativeCoordinates_RoundTrip()
        {
            // Window on a monitor to the left of / above the primary → negative origin.
            var original = new WindowPlacementRecord(-1920, -120, 1280, 720, false);

            Assert.True(WindowPlacementRecord.TryParse(original.Serialize(), out var parsed));
            Assert.Equal(original, parsed);
        }

        [Fact]
        public void Serialize_IsCultureInvariant()
        {
            var prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A culture that uses ',' as the decimal separator must not corrupt output.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var original = new WindowPlacementRecord(10, 20, 1234.5, 678.5, false);

                string text = original.Serialize();
                Assert.DoesNotContain(",", text);

                Assert.True(WindowPlacementRecord.TryParse(text, out var parsed));
                Assert.Equal(original, parsed);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]
        [InlineData("10;20;1400")]              // too few fields
        [InlineData("x;20;1400;900;0")]         // non-numeric x
        [InlineData("10;20;0;900;0")]           // zero width rejected
        [InlineData("10;20;1400;-5;0")]         // negative height rejected
        [InlineData("10;20;NaN;900;0")]         // NaN width rejected
        public void TryParse_RejectsInvalidInput(string? raw)
        {
            Assert.False(WindowPlacementRecord.TryParse(raw, out _));
        }

        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("True")]
        public void TryParse_AcceptsTruthyMaximizedTokens(string token)
        {
            Assert.True(WindowPlacementRecord.TryParse($"10;20;1400;900;{token}", out var r));
            Assert.True(r.Maximized);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("false")]
        [InlineData("anything-else")]
        public void TryParse_TreatsNonTruthyAsNotMaximized(string token)
        {
            Assert.True(WindowPlacementRecord.TryParse($"10;20;1400;900;{token}", out var r));
            Assert.False(r.Maximized);
        }

        // ── WindowPlacement.IsVisibleEnough ──────────────────────────────────────

        private static readonly List<(int X, int Y, int W, int H)> SingleScreen =
            new() { (0, 0, 1920, 1080) };

        // Primary at origin, second monitor to the LEFT (negative X).
        private static readonly List<(int X, int Y, int W, int H)> DualScreenLeft =
            new() { (0, 0, 1920, 1080), (-1920, 0, 1920, 1080) };

        [Fact]
        public void IsVisibleEnough_FullyOnScreen_True()
        {
            Assert.True(WindowPlacement.IsVisibleEnough(100, 100, 1400, 900, SingleScreen));
        }

        [Fact]
        public void IsVisibleEnough_FullyOffScreen_False()
        {
            // Way out to the right of the only monitor.
            Assert.False(WindowPlacement.IsVisibleEnough(5000, 5000, 1400, 900, SingleScreen));
        }

        [Fact]
        public void IsVisibleEnough_OnRemovedSecondMonitor_FalseWithOnlyPrimary()
        {
            // Window sits entirely on a left monitor (right edge at x = -400) that is
            // now unplugged → nothing overlaps the primary, so it's unreachable.
            Assert.False(WindowPlacement.IsVisibleEnough(-1800, 100, 1400, 900, SingleScreen));
        }

        [Fact]
        public void IsVisibleEnough_OnSecondMonitor_TrueWhenStillPresent()
        {
            Assert.True(WindowPlacement.IsVisibleEnough(-1800, 100, 1400, 900, DualScreenLeft));
        }

        [Fact]
        public void IsVisibleEnough_SliverOnScreen_BelowThreshold_False()
        {
            // Only 50 px of width pokes onto the screen — under the 120 px minimum.
            Assert.False(WindowPlacement.IsVisibleEnough(-1350, 100, 1400, 900, SingleScreen));
        }

        [Fact]
        public void IsVisibleEnough_GrabbableChunkOnScreen_AboveThreshold_True()
        {
            // 200 px of width remains on-screen — above the 120 px minimum.
            Assert.True(WindowPlacement.IsVisibleEnough(-1200, 100, 1400, 900, SingleScreen));
        }

        [Fact]
        public void IsVisibleEnough_NonPositiveSize_False()
        {
            Assert.False(WindowPlacement.IsVisibleEnough(100, 100, 0, 900, SingleScreen));
            Assert.False(WindowPlacement.IsVisibleEnough(100, 100, 1400, 0, SingleScreen));
        }

        [Fact]
        public void IsVisibleEnough_NoScreens_False()
        {
            Assert.False(WindowPlacement.IsVisibleEnough(100, 100, 1400, 900,
                new List<(int, int, int, int)>()));
        }

        // ── WindowPlacement.CenterIn ─────────────────────────────────────────────

        [Fact]
        public void CenterIn_CentersWithinScreen()
        {
            var (x, y) = WindowPlacement.CenterIn((0, 0, 1920, 1080), 1400, 900);
            Assert.Equal((1920 - 1400) / 2, x);
            Assert.Equal((1080 - 900) / 2, y);
        }

        [Fact]
        public void CenterIn_RespectsScreenOrigin()
        {
            // Secondary monitor offset to the right and down.
            var (x, y) = WindowPlacement.CenterIn((1920, 200, 1920, 1080), 1400, 900);
            Assert.Equal(1920 + (1920 - 1400) / 2, x);
            Assert.Equal(200 + (1080 - 900) / 2, y);
        }

        [Fact]
        public void CenterIn_WindowLargerThanScreen_ClampsToOrigin()
        {
            // Window bigger than the work area must not place its title bar off the top-left.
            var (x, y) = WindowPlacement.CenterIn((0, 0, 1000, 700), 1400, 900);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }
    }
}
