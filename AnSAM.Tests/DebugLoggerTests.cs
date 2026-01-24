using System;
using CommonUtilities;
using Xunit;

namespace AnSAM.Tests
{
    public class DebugLoggerTests
    {
        [Fact]
        public void LogDebug_DoesNotThrow_WhenHandlerThrows()
        {
            void Handler(string _) => throw new InvalidOperationException("boom");
#pragma warning disable CS0618 // Intentionally testing deprecated DebugLogger
            DebugLogger.OnLog += Handler;

            // Should not propagate exception from handler
            DebugLogger.LogDebug("test message");

            DebugLogger.OnLog -= Handler;
#pragma warning restore CS0618
        }
    }
}
