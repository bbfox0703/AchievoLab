using System;
using System.IO;
using CommonUtilities;
using Xunit;

namespace CommonUtilities.Tests
{
    /// <summary>
    /// Tests that <see cref="ImageValidation.Validate"/> separates "this is not an image" from
    /// "I could not read this file". Callers act destructively on the first — deleting the cached
    /// file, recording a download failure — and must not do so on the second.
    /// </summary>
    public class ImageValidationOutcomeTests : IDisposable
    {
        private readonly string _dir;

        public ImageValidationOutcomeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"ImageValidationOutcomeTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private string WriteFile(string name, byte[] bytes)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Fact]
        public void RealPngIsValid()
        {
            var path = WriteFile("ok.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            Assert.Equal(ImageValidationOutcome.Valid, ImageValidation.Validate(path));
            Assert.True(ImageValidation.IsValidImage(path));
        }

        [Fact]
        public void GarbageBytesAreInvalid()
        {
            var path = WriteFile("junk.png", new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 });

            Assert.Equal(ImageValidationOutcome.Invalid, ImageValidation.Validate(path));
        }

        [Fact]
        public void EmptyFileIsInvalid()
        {
            var path = WriteFile("empty.png", Array.Empty<byte>());

            Assert.Equal(ImageValidationOutcome.Invalid, ImageValidation.Validate(path));
        }

        [Fact]
        public void MissingFileIsInvalid()
        {
            Assert.Equal(ImageValidationOutcome.Invalid,
                ImageValidation.Validate(Path.Combine(_dir, "does-not-exist.png")));
        }

        /// <summary>
        /// A perfectly good image that happens to be exclusively locked must report Unreadable,
        /// not Invalid — reporting Invalid is what caused callers to delete good cached artwork.
        /// </summary>
        [Fact]
        public void ExclusivelyLockedFileIsUnreadableNotInvalid()
        {
            var path = WriteFile("locked.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Equal(ImageValidationOutcome.Unreadable, ImageValidation.Validate(path));
            Assert.False(ImageValidation.IsValidImage(path));
        }
    }
}
