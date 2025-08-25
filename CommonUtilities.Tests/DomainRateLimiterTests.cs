using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommonUtilities;
using Xunit;

public class DomainRateLimiterTests
{
    [Fact]
    public async Task WaitAsync_CancelDuringDomainDelay_ReleasesSemaphore()
    {
        var type = typeof(GameImageCache).Assembly.GetType("CommonUtilities.DomainRateLimiter", true)!;
        var limiter = Activator.CreateInstance(type, new object[] { 1, 60d, 1d, 60d, TimeSpan.FromMilliseconds(200), 0d })!;
        var uri = new Uri("http://example.com");
        var recordCall = type.GetMethod("RecordCall")!;
        recordCall.Invoke(limiter, new object[] { uri, true, null });

        var waitMethod = type.GetMethod("WaitAsync", new[] { typeof(Uri), typeof(CancellationToken) })!;

        using var cts = new CancellationTokenSource(50);
        var sw = Stopwatch.StartNew();
        var t1 = (Task)waitMethod.Invoke(limiter, new object[] { uri, cts.Token })!;
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await t1);
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 0, 500);

        var sw2 = Stopwatch.StartNew();
        await (Task)waitMethod.Invoke(limiter, new object[] { uri, CancellationToken.None })!;
        sw2.Stop();
        Assert.InRange(sw2.ElapsedMilliseconds, 100, 500);

        recordCall.Invoke(limiter, new object[] { uri, true, null });
    }

    [Fact]
    public async Task WaitAsync_CancelDuringTokenDelay_DoesNotWaitFullDelay()
    {
        var type = typeof(GameImageCache).Assembly.GetType("CommonUtilities.DomainRateLimiter", true)!;
        var limiter = Activator.CreateInstance(type, new object[] { 1, 1d, 5d, 0d, TimeSpan.Zero, 0d })!;
        var uri = new Uri("http://example.com");
        var waitMethod = type.GetMethod("WaitAsync", new[] { typeof(Uri), typeof(CancellationToken) })!;

        using var cts = new CancellationTokenSource(50);
        var sw = Stopwatch.StartNew();
        var t = (Task)waitMethod.Invoke(limiter, new object[] { uri, cts.Token })!;
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await t);
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 0, 150);

        await Task.Delay(250);

        var sw2 = Stopwatch.StartNew();
        await (Task)waitMethod.Invoke(limiter, new object[] { uri, CancellationToken.None })!;
        sw2.Stop();
        Assert.InRange(sw2.ElapsedMilliseconds, 0, 150);

        var recordCall = type.GetMethod("RecordCall")!;
        recordCall.Invoke(limiter, new object[] { uri, true, null });
    }
}
