using System;
using System.IO;
using CommonUtilities;
using Xunit;

public class ImageValidationTests
{
    public static TheoryData<byte[], string> ValidHeaders => new()
    {
        { new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ".png" },
        { new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, ".jpg" },
        { new byte[] { 0x47, 0x49, 0x46, 0x38 }, ".gif" },
        { new byte[] { 0x42, 0x4D, 0, 0 }, ".bmp" },
        { new byte[] { 0x00, 0x00, 0x01, 0x00, 0, 0 }, ".ico" },
        { new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 }, ".avif" },
        { new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, ".webp" },
    };

    [Theory]
    [MemberData(nameof(ValidHeaders))]
    public void SupportedFormatsAreValid(byte[] data, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ext);
        File.WriteAllBytes(path, data);
        try
        {
            Assert.True(ImageValidation.IsValidImage(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidImageIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        try
        {
            Assert.False(ImageValidation.IsValidImage(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

