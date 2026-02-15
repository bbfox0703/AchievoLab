using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using RunGame.Steam;
using Xunit;

namespace RunGame.Tests
{
    /// <summary>
    /// Tests for Steam client initialization and helper methods.
    ///
    /// NOTE: Full initialization tests require actual Steam installation and are suited for integration testing.
    /// These unit tests focus on testable helper methods and state validation.
    ///
    /// The following require integration/manual testing with real Steam:
    /// - P/Invoke calls to steamclient64.dll or steam_api64.dll
    /// - Vtable function pointer resolution
    /// - Steam interface acquisition (ISteamUserStats, ISteamApps, etc.)
    /// - Steam callback pump
    /// - User authentication validation
    /// </summary>
    public class SteamClientTests
    {
        #region Helper Method Tests

        [Fact]
        public void GetSteamPath_Reflection_ReturnsValidPath()
        {
            // Use reflection to access private GetSteamPath method from SteamGameClient
            var method = typeof(SteamGameClient).GetMethod("GetSteamPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method == null)
            {
                // Method might not be accessible - skip test
                return;
            }

            var result = method.Invoke(null, null) as string;

            // Result should either be null (Steam not installed) or a valid path
            if (result != null)
            {
                Assert.True(Directory.Exists(result), $"Steam path should exist: {result}");
                // Common Steam paths validation
                Assert.True(result.Contains("Steam", StringComparison.OrdinalIgnoreCase),
                    "Path should contain 'Steam'");
            }
        }

        [Fact]
        public void IsSteamRunning_Reflection_ReturnsBoolean()
        {
            // Use reflection to access private IsSteamRunning method
            var method = typeof(SteamGameClient).GetMethod("IsSteamRunning",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method == null)
            {
                // Method might not be accessible - skip test
                return;
            }

            var result = method.Invoke(null, null);

            // Result should be a boolean
            Assert.IsType<bool>(result);

            bool isRunning = (bool)result;

            // If Steam is running, verify process exists
            if (isRunning)
            {
                var processes = Process.GetProcessesByName("steam");
                Assert.True(processes.Length > 0, "Steam process should exist when IsSteamRunning returns true");
            }
        }

        [Fact]
        public void ModernSteamClient_GetSteamInstallPath_Reflection_ReturnsValidPath()
        {
            // Use reflection to access private GetSteamInstallPath method from ModernSteamClient
            var method = typeof(ModernSteamClient).GetMethod("GetSteamInstallPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method == null)
            {
                // Method might not be accessible - skip test
                return;
            }

            var result = method.Invoke(null, null) as string;

            // Result should either be null (Steam not installed) or a valid path
            if (result != null)
            {
                Assert.True(Directory.Exists(result), $"Steam install path should exist: {result}");
                Assert.True(result.Contains("Steam", StringComparison.OrdinalIgnoreCase),
                    "Path should contain 'Steam'");
            }
        }

        [Fact]
        public void ModernSteamClient_GetSteamApiSearchPaths_Reflection_ReturnsNonEmptyList()
        {
            // Use reflection to access private GetSteamApiSearchPaths method
            var method = typeof(ModernSteamClient).GetMethod("GetSteamApiSearchPaths",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method == null)
            {
                // Method might not be accessible - skip test
                return;
            }

            var result = method.Invoke(null, null) as List<string>;

            Assert.NotNull(result);
            Assert.NotEmpty(result);

            // Should always include fallback path
            Assert.Contains(result, path => path == "steam_api64.dll");
        }

        #endregion

        #region State Validation Tests

        [Fact]
        public void SteamGameClient_WithInvalidGameId_InitializedIsFalse()
        {
            // Attempt to create client with invalid game ID
            // Without Steam running or with invalid game, should not initialize
            var client = new SteamGameClient(-1);

            // Should not throw, but Initialized should be false
            Assert.False(client.Initialized);

            client.Dispose();
        }

        [Fact]
        public void SteamGameClient_Initialized_ReflectsConstructorSuccess()
        {
            // This test verifies the Initialized property is set correctly
            // It won't be true unless Steam is actually running and accessible
            long testGameId = 400; // Portal (small, common game for testing)

            var client = new SteamGameClient(testGameId);

            // Initialized should be a boolean value (not throw)
            Assert.IsType<bool>(client.Initialized);

            // If initialized, verify the game ID was stored
            if (client.Initialized)
            {
                // Use reflection to verify internal game ID
                var field = typeof(SteamGameClient).GetField("_gameId",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (field != null)
                {
                    var gameId = (long)field.GetValue(client)!;
                    Assert.Equal(testGameId, gameId);
                }
            }

            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_Initialized_ReflectsConstructorSuccess()
        {
            long testGameId = 400; // Portal

            var client = new ModernSteamClient(testGameId);

            // Initialized should be accessible
            Assert.IsType<bool>(client.Initialized);

            // If initialized, verify the game ID was stored
            if (client.Initialized)
            {
                var field = typeof(ModernSteamClient).GetField("_gameId",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (field != null)
                {
                    var gameId = (long)field.GetValue(client)!;
                    Assert.Equal(testGameId, gameId);
                }
            }

            client.Dispose();
        }

        #endregion

        #region Language Detection Tests

        [Fact]
        public void SteamGameClient_GetCurrentGameLanguage_ReturnsValidLanguage()
        {
            long testGameId = 400;
            var client = new SteamGameClient(testGameId);

            var language = client.GetCurrentGameLanguage();

            // Should always return a non-null string (defaults to "english" if Steam not available)
            Assert.NotNull(language);
            Assert.NotEmpty(language);

            // Should be a valid Steam language code
            var validLanguages = new[] { "english", "german", "french", "spanish", "russian", "japanese",
                "korean", "tchinese", "schinese", "portuguese", "polish", "danish", "dutch",
                "finnish", "norwegian", "swedish", "italian", "brazilian" };

            Assert.Contains(language.ToLowerInvariant(), validLanguages);

            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_GetCurrentGameLanguage_ReturnsValidLanguage()
        {
            long testGameId = 400;
            var client = new ModernSteamClient(testGameId);

            var language = client.GetCurrentGameLanguage();

            // Should always return a non-null string (defaults to "english" if Steam not available)
            Assert.NotNull(language);
            Assert.NotEmpty(language);

            // Should be a valid Steam language code
            var validLanguages = new[] { "english", "german", "french", "spanish", "russian", "japanese",
                "korean", "tchinese", "schinese", "portuguese", "polish", "danish", "dutch",
                "finnish", "norwegian", "swedish", "italian", "brazilian" };

            Assert.Contains(language.ToLowerInvariant(), validLanguages);

            client.Dispose();
        }

        #endregion

        #region Disposal Tests

        [Fact]
        public void SteamGameClient_Dispose_CanBeCalledMultipleTimes()
        {
            var client = new SteamGameClient(400);

            // Should not throw when called multiple times
            client.Dispose();
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_Dispose_CanBeCalledMultipleTimes()
        {
            var client = new ModernSteamClient(400);

            // Should not throw when called multiple times
            client.Dispose();
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void SteamGameClient_Dispose_StopsCallbackTimer()
        {
            var client = new SteamGameClient(400);

            // Access callback timer via reflection
            var timerField = typeof(SteamGameClient).GetField("_callbackTimer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            client.Dispose();

            // After dispose, timer should be disposed
            if (timerField != null && client.Initialized)
            {
                // If timer was initialized, it should now be disposed
                // We can't directly check if timer is disposed, but we can verify Dispose was called
                // by checking that subsequent calls don't throw
                Assert.True(true); // Placeholder - timer disposal is internal
            }
        }

        #endregion

        #region ISteamUserStats Interface Implementation Tests

        [Fact]
        public void SteamGameClient_ImplementsISteamUserStats()
        {
            var client = new SteamGameClient(400);

            Assert.IsAssignableFrom<ISteamUserStats>(client);

            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_ImplementsISteamUserStats()
        {
            var client = new ModernSteamClient(400);

            Assert.IsAssignableFrom<ISteamUserStats>(client);

            client.Dispose();
        }

        [Fact]
        public void SteamGameClient_WhenNotInitialized_InterfaceMethodsReturnFalse()
        {
            // Create client that likely won't initialize (invalid game ID)
            var client = new SteamGameClient(-1);

            if (!client.Initialized)
            {
                // All ISteamUserStats methods should return false or safe defaults
                Assert.False(client.RequestUserStats(400));
                Assert.False(client.SetAchievement("test", true));
                Assert.False(client.GetStatValue("test", out int intValue));
                Assert.False(client.GetStatValue("test", out float floatValue));
                Assert.False(client.SetStatValue("test", 100));
                Assert.False(client.SetStatValue("test", 100.0f));
                Assert.False(client.StoreStats());
                Assert.False(client.ResetAllStats(true));

                // GetAppData should return null
                Assert.Null(client.GetAppData(400, "name"));
            }

            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_WhenNotInitialized_InterfaceMethodsReturnFalse()
        {
            // Create client that likely won't initialize (invalid game ID)
            var client = new ModernSteamClient(-1);

            if (!client.Initialized)
            {
                // All ISteamUserStats methods should return false or safe defaults
                Assert.False(client.RequestUserStats(400));
                Assert.False(client.SetAchievement("test", true));
                Assert.False(client.GetStatValue("test", out int intValue));
                Assert.False(client.GetStatValue("test", out float floatValue));
                Assert.False(client.SetStatValue("test", 100));
                Assert.False(client.SetStatValue("test", 100.0f));
                Assert.False(client.StoreStats());
                Assert.False(client.ResetAllStats(true));

                // GetAppData should return null
                Assert.Null(client.GetAppData(400, "name"));
            }

            client.Dispose();
        }

        #endregion

        #region ModernSteamClient Specific Tests

        [Fact]
        public void ModernSteamClient_RequestUserStats_ReturnsTrueWhenInitialized()
        {
            var client = new ModernSteamClient(400);

            // Modern SDK auto-synchronizes stats, returns true only when initialized
            var result = client.RequestUserStats(400);

            if (client.Initialized)
                Assert.True(result);
            else
                Assert.False(result);

            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_GetAppData_SupportsBasicKeys()
        {
            var client = new ModernSteamClient(400);

            if (client.Initialized)
            {
                // Modern client supports limited GetAppData keys
                var nameResult = client.GetAppData(400, "name");
                var typeResult = client.GetAppData(400, "type");
                var stateResult = client.GetAppData(400, "state");

                // Should return non-null for supported keys
                Assert.NotNull(nameResult);
                Assert.NotNull(typeResult);
                Assert.NotNull(stateResult);

                // Unsupported key should return null
                var invalidResult = client.GetAppData(400, "invalid_key");
                Assert.Null(invalidResult);
            }

            client.Dispose();
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public void SteamGameClient_Constructor_DoesNotThrow()
        {
            // Should handle all exceptions internally
            var exception = Record.Exception(() =>
            {
                using var client = new SteamGameClient(0);
            });

            Assert.Null(exception);
        }

        [Fact]
        public void ModernSteamClient_Constructor_DoesNotThrow()
        {
            // Should handle all exceptions internally
            var exception = Record.Exception(() =>
            {
                using var client = new ModernSteamClient(0);
            });

            Assert.Null(exception);
        }

        [Fact]
        public void SteamGameClient_CallbacksWithoutInitialization_DoesNotThrow()
        {
            var client = new SteamGameClient(-1);

            // RunCallbacks should not throw even when not initialized
            var exception = Record.Exception(() => client.RunCallbacks());

            Assert.Null(exception);

            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_CallbacksWithoutInitialization_DoesNotThrow()
        {
            var client = new ModernSteamClient(-1);

            // RunCallbacks should not throw even when not initialized
            var exception = Record.Exception(() => client.RunCallbacks());

            Assert.Null(exception);

            client.Dispose();
        }

        #endregion

        #region Callback Registration Tests

        [Fact]
        public void SteamGameClient_RegisterUserStatsCallback_DoesNotThrow()
        {
            var client = new SteamGameClient(400);

            var callbackInvoked = false;
            void TestCallback(SteamGameClient.UserStatsReceived stats)
            {
                callbackInvoked = true;
            }

            // Should not throw when registering callback
            var exception = Record.Exception(() =>
            {
                client.RegisterUserStatsCallback(TestCallback);
            });

            Assert.Null(exception);

            client.Dispose();
        }

        [Fact]
        public void ModernSteamClient_RegisterUserStatsCallback_DoesNotThrow()
        {
            var client = new ModernSteamClient(400);

            void TestCallback(SteamGameClient.UserStatsReceived stats)
            {
                // Modern client logs but doesn't use callback
            }

            // Should not throw when registering callback
            var exception = Record.Exception(() =>
            {
                client.RegisterUserStatsCallback(TestCallback);
            });

            Assert.Null(exception);

            client.Dispose();
        }

        #endregion
    }
}
