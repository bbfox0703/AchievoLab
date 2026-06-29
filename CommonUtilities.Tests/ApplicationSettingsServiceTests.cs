using System;
using System.IO;
using CommonUtilities;
using Xunit;

namespace CommonUtilities.Tests
{
    public class ApplicationSettingsServiceTests
    {
        private static string IsolatedAppName() => "AchievoLab_Test_" + Guid.NewGuid().ToString("N");

        private static string FolderFor(string appName) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);

        [Fact]
        public void ConcurrentInstances_DoNotClobberEachOthersKeys()
        {
            var appName = IsolatedAppName();
            try
            {
                // Two services that both loaded the (empty) file up-front — models AnSAM
                // and a child RunGame each holding their own in-memory snapshot.
                var a = new ApplicationSettingsService();
                a.Initialize(appName);
                var b = new ApplicationSettingsService();
                b.Initialize(appName);

                a.TrySetString("Window.AnSAM", "1");
                // b's snapshot never contained Window.AnSAM; before the merge fix this
                // whole-file rewrite would have reverted it.
                b.TrySetString("Window.RunGame", "2");

                var reader = new ApplicationSettingsService();
                reader.Initialize(appName);

                Assert.True(reader.TryGetString("Window.AnSAM", out var va));
                Assert.Equal("1", va);
                Assert.True(reader.TryGetString("Window.RunGame", out var vb));
                Assert.Equal("2", vb);
            }
            finally
            {
                TryDelete(FolderFor(appName));
            }
        }

        [Fact]
        public void Remove_FromOneInstance_DoesNotResurrectViaAnother()
        {
            var appName = IsolatedAppName();
            try
            {
                var a = new ApplicationSettingsService();
                a.Initialize(appName);
                var b = new ApplicationSettingsService();
                b.Initialize(appName);

                a.TrySetString("Keep", "x");
                a.TrySetString("Drop", "y");

                // b changes an unrelated key, then a removes Drop. b's later write must
                // not bring Drop back.
                b.TrySetString("Other", "z");
                a.TryRemove("Drop");
                b.TrySetString("Other", "z2");

                var reader = new ApplicationSettingsService();
                reader.Initialize(appName);

                Assert.True(reader.TryGetString("Keep", out var keep) && keep == "x");
                Assert.True(reader.TryGetString("Other", out var other) && other == "z2");
                Assert.False(reader.ContainsKey("Drop"));
            }
            finally
            {
                TryDelete(FolderFor(appName));
            }
        }

        [Fact]
        public void SetThenGet_RoundTrips_AndPersistsAcrossInstances()
        {
            var appName = IsolatedAppName();
            try
            {
                var writer = new ApplicationSettingsService();
                writer.Initialize(appName);
                writer.TrySetInt("Width", 1234);
                writer.TrySetBool("Maximized", true);

                var reader = new ApplicationSettingsService();
                reader.Initialize(appName);

                Assert.True(reader.TryGetInt("Width", out var w));
                Assert.Equal(1234, w);
                Assert.True(reader.TryGetBool("Maximized", out var m));
                Assert.True(m);
            }
            finally
            {
                TryDelete(FolderFor(appName));
            }
        }

        private static void TryDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
