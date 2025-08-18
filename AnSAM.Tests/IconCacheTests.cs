using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AnSAM.Services;
using Xunit;

public class IconCacheTests
{
    public static IEnumerable<object[]> ValidHeaders()
    {
        yield return new object[] { "bmp", new byte[] { 0x42, 0x4D, 0, 0, 0, 0 } };
        yield return new object[] { "ico", new byte[] { 0x00, 0x00, 0x01, 0x00, 0, 0 } };
        yield return new object[] { "avif", new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66 } };
    }

    [Theory]
    [MemberData(nameof(ValidHeaders))]
    public async Task ValidCachedIconIsUsed(string ext, byte[] data)
    {
        var id = Random.Shared.Next(100000, 200000);
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnSAM", "appcache");
        Directory.CreateDirectory(cacheDir);
        foreach (var file in Directory.EnumerateFiles(cacheDir, $"{id}.*"))
        {
            try { File.Delete(file); } catch { }
        }
        var path = Path.Combine(cacheDir, $"{id}.{ext}");
        await File.WriteAllBytesAsync(path, data);
        var uri = new Uri($"http://example.invalid/{id}.{ext}");
        var result = await IconCache.GetIconPathAsync(id, uri);
        Assert.Equal(path, result.Path);
        Assert.False(result.Downloaded);
        try { File.Delete(path); } catch { }
    }
}
