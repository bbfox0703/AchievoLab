using System;
using System.IO;
using System.Threading.Tasks;
using RunGame.Utils;
using Xunit;

namespace RunGame.Tests
{
    /// <summary>
    /// Regression tests for binary VDF parsing against corrupt or truncated
    /// UserGameStatsSchema files. Both cases below used to take down RunGame:
    /// a truncated file spun forever inside ReadStringUnicode (frozen UI, only
    /// killable from Task Manager), and a run of 0x00 bytes recursed until the
    /// stack overflowed — an exception that cannot be caught, so the process died.
    ///
    /// Every test is time-bounded: a regression must fail the run, not hang it.
    /// </summary>
    public class KeyValueRobustnessTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Runs <paramref name="action"/> and fails (rather than hangs) if it does not finish.
        /// </summary>
        private static T RunBounded<T>(Func<T> action)
        {
            var task = Task.Run(action);
            Assert.True(task.Wait(Budget), "Parsing did not terminate — the loop guard has regressed.");
            return task.Result;
        }

        [Fact]
        public void ReadStringUnicode_WithoutTerminator_ThrowsInsteadOfLoopingForever()
        {
            // No trailing 0x00: ReadByte returns -1 at EOF, which used to be cast to
            // 0xFF and never matched the terminator.
            var data = new byte[] { (byte)'a', (byte)'b', (byte)'c' };

            RunBounded<object?>(() =>
            {
                using var stream = new MemoryStream(data);
                Assert.Throws<EndOfStreamException>(() => stream.ReadStringUnicode());
                return null;
            });
        }

        [Fact]
        public void ReadStringUnicode_WithTerminator_ReadsValue()
        {
            var data = new byte[] { (byte)'a', (byte)'b', (byte)'c', 0x00 };

            using var stream = new MemoryStream(data);

            Assert.Equal("abc", stream.ReadStringUnicode());
        }

        [Fact]
        public void ReadStringUnicode_StopsAtTerminatorAndLeavesRest()
        {
            var data = new byte[] { (byte)'h', (byte)'i', 0x00, (byte)'x' };

            using var stream = new MemoryStream(data);

            Assert.Equal("hi", stream.ReadStringUnicode());
            Assert.Equal((byte)'x', (byte)stream.ReadByte());
        }

        [Fact]
        public void ReadAsBinary_TruncatedStream_ReturnsFalse()
        {
            // Type byte says "string" and the name is terminated, but the value's
            // terminator never arrives.
            var data = new byte[] { 0x01, (byte)'k', (byte)'e', (byte)'y', 0x00, (byte)'v', (byte)'a', (byte)'l' };

            var result = RunBounded(() =>
            {
                using var stream = new MemoryStream(data);
                return new KeyValue().ReadAsBinary(stream);
            });

            Assert.False(result);
        }

        [Fact]
        public void ReadAsBinary_LongRunOfZeros_ReturnsFalseWithoutStackOverflow()
        {
            // Every 0x00 pair parses as another unnamed nested container. Without the
            // depth cap this recursed once per two bytes and blew the stack.
            var data = new byte[64 * 1024];

            var result = RunBounded(() =>
            {
                using var stream = new MemoryStream(data);
                return new KeyValue().ReadAsBinary(stream);
            });

            Assert.False(result);
        }

        [Fact]
        public void LoadAsBinary_CorruptFile_ReturnsNullWithoutCrashing()
        {
            var path = Path.Combine(Path.GetTempPath(), $"KeyValueRobustness_{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(path, new byte[32 * 1024]);

            try
            {
                var result = RunBounded(() => KeyValue.LoadAsBinary(path));

                Assert.Null(result);
            }
            finally
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public void ReadAsBinary_WellFormedData_StillParses()
        {
            // Guards against the robustness fixes breaking the happy path:
            // String "name" = "Portal", then the End marker.
            using var ms = new MemoryStream();
            ms.WriteByte(0x01); // KeyValueType.String
            WriteCString(ms, "name");
            WriteCString(ms, "Portal");
            ms.WriteByte(0x08); // KeyValueType.End
            ms.Position = 0;

            var kv = new KeyValue();

            Assert.True(kv.ReadAsBinary(ms));
            Assert.Single(kv.Children);
            Assert.Equal("name", kv.Children[0].Name);
            Assert.Equal("Portal", kv.Children[0].Value);
        }

        private static void WriteCString(Stream stream, string value)
        {
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
            {
                stream.WriteByte(b);
            }
            stream.WriteByte(0x00);
        }
    }
}
