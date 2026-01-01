# CDN Failover 策略實作指南

## 概述

實作智能 CDN 負載均衡器，自動選擇最佳可用的 CDN，避免單一 CDN 過載或被 block。

## 實作步驟

### 步驟 1：新增 CdnLoadBalancer 類別

**檔案位置：** `CommonUtilities/CdnLoadBalancer.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CommonUtilities
{
    /// <summary>
    /// CDN 負載均衡器，根據當前並發數和失敗記錄選擇最佳 CDN
    /// </summary>
    public class CdnLoadBalancer
    {
        private readonly ConcurrentDictionary<string, int> _activeRequests = new();
        private readonly ConcurrentDictionary<string, DateTime> _blockedUntil = new();
        private readonly ConcurrentDictionary<string, CdnStats> _stats = new();
        private readonly int _maxConcurrentPerDomain;
        private readonly object _lock = new();

        public CdnLoadBalancer(int maxConcurrentPerDomain = 2)
        {
            _maxConcurrentPerDomain = maxConcurrentPerDomain;
        }

        /// <summary>
        /// 從 URL 列表中選擇最佳 CDN
        /// </summary>
        public string SelectBestCdn(List<string> cdnUrls)
        {
            if (cdnUrls == null || cdnUrls.Count == 0)
                throw new ArgumentException("CDN URL list cannot be empty", nameof(cdnUrls));

            var now = DateTime.UtcNow;

            // 評估每個 CDN 的可用性
            var candidates = cdnUrls
                .Select(url =>
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        return null;

                    var domain = uri.Host;
                    var activeCount = _activeRequests.GetOrAdd(domain, 0);
                    var stats = _stats.GetOrAdd(domain, _ => new CdnStats());

                    // 檢查是否被 block
                    bool isBlocked = false;
                    if (_blockedUntil.TryGetValue(domain, out var blockedTime))
                    {
                        if (now < blockedTime)
                        {
                            isBlocked = true;
                            DebugLogger.LogDebug($"CDN {domain} is blocked until {blockedTime}");
                        }
                        else
                        {
                            // 已過期，移除 block 記錄
                            _blockedUntil.TryRemove(domain, out _);
                        }
                    }

                    return new
                    {
                        Url = url,
                        Domain = domain,
                        ActiveCount = activeCount,
                        IsBlocked = isBlocked,
                        IsAvailable = !isBlocked && activeCount < _maxConcurrentPerDomain,
                        Priority = GetDomainPriority(domain),
                        SuccessRate = stats.GetSuccessRate()
                    };
                })
                .Where(x => x != null)
                .ToList();

            // 選擇策略：
            // 1. 優先選擇可用的 CDN（未 block 且未達並發上限）
            // 2. 按優先級排序（CloudFlare > Steam > Akamai）
            // 3. 按成功率排序
            // 4. 按當前並發數排序（選擇最少的）
            var best = candidates
                .Where(x => x.IsAvailable)
                .OrderByDescending(x => x.Priority)
                .ThenByDescending(x => x.SuccessRate)
                .ThenBy(x => x.ActiveCount)
                .ThenBy(x => Guid.NewGuid()) // 相同條件時隨機選擇
                .FirstOrDefault();

            if (best != null)
            {
                DebugLogger.LogDebug($"Selected CDN: {best.Domain} (Active: {best.ActiveCount}, Priority: {best.Priority}, Success Rate: {best.SuccessRate:P})");
                return best.Url;
            }

            // 如果所有 CDN 都不可用，選擇最快恢復的
            var leastBusy = candidates
                .Where(x => !x.IsBlocked)
                .OrderBy(x => x.ActiveCount)
                .FirstOrDefault();

            if (leastBusy != null)
            {
                DebugLogger.LogDebug($"All CDNs busy, selecting least busy: {leastBusy.Domain} (Active: {leastBusy.ActiveCount})");
                return leastBusy.Url;
            }

            // Fallback：返回第一個
            DebugLogger.LogDebug("All CDNs blocked or unavailable, using first URL as fallback");
            return cdnUrls[0];
        }

        /// <summary>
        /// 獲取域名的優先級（1=最高）
        /// </summary>
        private int GetDomainPriority(string domain)
        {
            // CloudFlare 優先（通常品質最好）
            if (domain.Contains("cloudflare"))
                return 3;

            // Steam CDN 次之
            if (domain.Contains("cdn.steamstatic.com"))
                return 2;

            // Akamai 最後
            if (domain.Contains("akamai"))
                return 1;

            return 0;
        }

        /// <summary>
        /// 記錄開始使用某個域名
        /// </summary>
        public void IncrementActiveRequests(string domain)
        {
            _activeRequests.AddOrUpdate(domain, 1, (_, count) => count + 1);
            DebugLogger.LogDebug($"CDN {domain} active requests: {_activeRequests[domain]}");
        }

        /// <summary>
        /// 記錄完成使用某個域名
        /// </summary>
        public void DecrementActiveRequests(string domain)
        {
            _activeRequests.AddOrUpdate(domain, 0, (_, count) => Math.Max(0, count - 1));
            DebugLogger.LogDebug($"CDN {domain} active requests: {_activeRequests[domain]}");
        }

        /// <summary>
        /// 記錄 CDN 被阻擋（429/403）
        /// </summary>
        public void RecordBlockedDomain(string domain, TimeSpan? duration = null)
        {
            var blockDuration = duration ?? TimeSpan.FromMinutes(5);
            _blockedUntil[domain] = DateTime.UtcNow.Add(blockDuration);
            DebugLogger.LogDebug($"CDN {domain} blocked for {blockDuration.TotalMinutes} minutes");

            // 記錄到統計
            var stats = _stats.GetOrAdd(domain, _ => new CdnStats());
            stats.RecordFailure();
        }

        /// <summary>
        /// 記錄成功的下載
        /// </summary>
        public void RecordSuccess(string domain)
        {
            var stats = _stats.GetOrAdd(domain, _ => new CdnStats());
            stats.RecordSuccess();
        }

        /// <summary>
        /// 記錄失敗的下載
        /// </summary>
        public void RecordFailure(string domain)
        {
            var stats = _stats.GetOrAdd(domain, _ => new CdnStats());
            stats.RecordFailure();
        }

        /// <summary>
        /// 獲取所有 CDN 的統計資訊
        /// </summary>
        public Dictionary<string, (int Active, bool IsBlocked, double SuccessRate)> GetStats()
        {
            var now = DateTime.UtcNow;
            return _stats.ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    var active = _activeRequests.GetOrAdd(kvp.Key, 0);
                    var isBlocked = _blockedUntil.TryGetValue(kvp.Key, out var blockedTime) && now < blockedTime;
                    var successRate = kvp.Value.GetSuccessRate();
                    return (active, isBlocked, successRate);
                });
        }

        /// <summary>
        /// CDN 統計資訊
        /// </summary>
        private class CdnStats
        {
            private int _totalRequests;
            private int _successCount;
            private readonly object _lock = new();

            public void RecordSuccess()
            {
                lock (_lock)
                {
                    _totalRequests++;
                    _successCount++;
                }
            }

            public void RecordFailure()
            {
                lock (_lock)
                {
                    _totalRequests++;
                }
            }

            public double GetSuccessRate()
            {
                lock (_lock)
                {
                    if (_totalRequests == 0)
                        return 1.0; // 假設新 CDN 100% 成功率

                    return (double)_successCount / _totalRequests;
                }
            }
        }
    }
}
```

---

### 步驟 2：整合到 SharedImageService

**檔案位置：** `CommonUtilities/SharedImageService.cs`

#### 2.1 新增成員變數

```csharp
public class SharedImageService : IDisposable
{
    // ... 現有成員 ...

    private readonly CdnLoadBalancer _cdnLoadBalancer;

    public SharedImageService(HttpClient httpClient, GameImageCache? cache = null, bool disposeHttpClient = false)
    {
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _cdnLoadBalancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);

        // ... 現有程式碼 ...
    }
}
```

#### 2.2 修改 TryDownloadLanguageSpecificImageAsync

**替換整個方法：**

```csharp
private async Task<string?> TryDownloadLanguageSpecificImageAsync(int appId, string language, string cacheKey)
{
    // Wait for available download slot
    await _downloadSemaphore.WaitAsync(_cts.Token);
    var pending = _pendingRequests.Count;
    var available = _downloadSemaphore.CurrentCount;
    DebugLogger.LogDebug($"Starting download for {appId} ({language}) - Pending: {pending}, Available slots: {available}");

    try
    {
        var languageSpecificUrlMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        static void AddUrl(Dictionary<string, List<string>> map, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var domain = uri.Host;
                if (!map.TryGetValue(domain, out var list))
                {
                    list = new List<string>();
                    map[domain] = list;
                }
                list.Add(url);
            }
        }

        static List<string> RoundRobin(Dictionary<string, List<string>> map)
        {
            var domainQueues = map.ToDictionary(kv => kv.Key, kv => new Queue<string>(kv.Value));
            var keys = domainQueues.Keys.ToList();
            var result = new List<string>();
            bool added;
            do
            {
                added = false;
                foreach (var key in keys)
                {
                    var queue = domainQueues[key];
                    if (queue.Count > 0)
                    {
                        result.Add(queue.Dequeue());
                        added = true;
                    }
                }
            } while (added);
            return result;
        }

        var header = await GetHeaderImageFromStoreApiAsync(appId, language, _cts.Token);
        if (!string.IsNullOrEmpty(header))
        {
            AddUrl(languageSpecificUrlMap, header);
        }

        if (string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
        {
            AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");
            AddUrl(languageSpecificUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg");
            AddUrl(languageSpecificUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");
        }
        else
        {
            AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");
            AddUrl(languageSpecificUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header_{language}.jpg");
            AddUrl(languageSpecificUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header_{language}.jpg");
        }

        var languageUrls = RoundRobin(languageSpecificUrlMap);

        // === 新增：使用 CDN 負載均衡器 ===
        var result = await TryDownloadWithCdnFailover(appId.ToString(), languageUrls, language, appId, _cts.Token);

        if (!string.IsNullOrEmpty(result?.Path) && IsFreshImage(result.Value.Path))
        {
            _imageCache[cacheKey] = result.Value.Path;
            if (result.Value.Downloaded)
            {
                TriggerImageDownloadCompletedEvent(appId, result.Value.Path);
            }
            return result.Value.Path;
        }

        // Clean up any invalid result
        if (!string.IsNullOrEmpty(result?.Path))
        {
            try { File.Delete(result.Value.Path); } catch { }
        }

        return null;
    }
    finally
    {
        _downloadSemaphore.Release();
    }
}
```

#### 2.3 新增 TryDownloadWithCdnFailover 方法

```csharp
/// <summary>
/// 使用 CDN Failover 策略下載圖片
/// </summary>
private async Task<GameImageCache.ImageResult?> TryDownloadWithCdnFailover(
    string cacheKey,
    List<string> cdnUrls,
    string language,
    int? failureId,
    CancellationToken cancellationToken)
{
    if (cdnUrls == null || cdnUrls.Count == 0)
        return null;

    // 嘗試最多 3 次（每個 CDN 至少嘗試一次）
    for (int attempt = 0; attempt < Math.Min(3, cdnUrls.Count); attempt++)
    {
        // 選擇最佳 CDN
        var selectedUrl = _cdnLoadBalancer.SelectBestCdn(cdnUrls);

        if (!Uri.TryCreate(selectedUrl, UriKind.Absolute, out var uri))
        {
            DebugLogger.LogDebug($"Invalid URL: {selectedUrl}");
            continue;
        }

        var domain = uri.Host;
        _cdnLoadBalancer.IncrementActiveRequests(domain);

        try
        {
            DebugLogger.LogDebug($"Attempting download from {domain} (attempt {attempt + 1})");

            var result = await _cache.GetImagePathAsync(
                cacheKey,
                uri,
                language,
                failureId,
                cancellationToken,
                checkEnglishFallback: false);

            if (!string.IsNullOrEmpty(result.Path))
            {
                _cdnLoadBalancer.RecordSuccess(domain);
                DebugLogger.LogDebug($"Successfully downloaded from {domain}");
                return result;
            }
            else
            {
                _cdnLoadBalancer.RecordFailure(domain);
                DebugLogger.LogDebug($"Download failed from {domain} (empty result)");
            }
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            // CDN 返回限速錯誤，標記為 blocked
            _cdnLoadBalancer.RecordBlockedDomain(domain, TimeSpan.FromMinutes(5));
            DebugLogger.LogDebug($"CDN {domain} returned rate limit error, marking as blocked");

            // 從候選列表中移除這個 URL，避免重複嘗試
            cdnUrls.Remove(selectedUrl);
        }
        catch (Exception ex)
        {
            _cdnLoadBalancer.RecordFailure(domain);
            DebugLogger.LogDebug($"Download error from {domain}: {ex.Message}");
        }
        finally
        {
            _cdnLoadBalancer.DecrementActiveRequests(domain);
        }
    }

    // 所有 CDN 都失敗
    return null;
}
```

#### 2.4 同樣修改 TryDownloadEnglishImageAsync

**替換整個方法：**

```csharp
private async Task<string?> TryDownloadEnglishImageAsync(int appId, string cacheKey)
{
    // Wait for available download slot
    await _downloadSemaphore.WaitAsync(_cts.Token);
    var pending = _pendingRequests.Count;
    var available = _downloadSemaphore.CurrentCount;
    DebugLogger.LogDebug($"Starting English download for {appId} - Pending: {pending}, Available slots: {available}");

    try
    {
        var languageSpecificUrlMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        static void AddUrl(Dictionary<string, List<string>> map, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var domain = uri.Host;
                if (!map.TryGetValue(domain, out var list))
                {
                    list = new List<string>();
                    map[domain] = list;
                }
                list.Add(url);
            }
        }

        static List<string> RoundRobin(Dictionary<string, List<string>> map)
        {
            var domainQueues = map.ToDictionary(kv => kv.Key, kv => new Queue<string>(kv.Value));
            var keys = domainQueues.Keys.ToList();
            var result = new List<string>();
            bool added;
            do
            {
                added = false;
                foreach (var key in keys)
                {
                    var queue = domainQueues[key];
                    if (queue.Count > 0)
                    {
                        result.Add(queue.Dequeue());
                        added = true;
                    }
                }
            } while (added);
            return result;
        }

        // Get English header from Store API
        var header = await GetHeaderImageFromStoreApiAsync(appId, "english", _cts.Token);
        if (!string.IsNullOrEmpty(header))
        {
            AddUrl(languageSpecificUrlMap, header);
        }

        AddUrl(languageSpecificUrlMap, $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");
        AddUrl(languageSpecificUrlMap, $"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg");
        AddUrl(languageSpecificUrlMap, $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");

        var englishUrls = RoundRobin(languageSpecificUrlMap);

        // === 使用 CDN 負載均衡器 ===
        var result = await TryDownloadWithCdnFailover(appId.ToString(), englishUrls, "english", appId, _cts.Token);

        if (!string.IsNullOrEmpty(result?.Path) && IsFreshImage(result.Value.Path))
        {
            _imageCache[cacheKey] = result.Value.Path;
            if (result.Value.Downloaded)
            {
                TriggerImageDownloadCompletedEvent(appId, result.Value.Path);
            }
            return result.Value.Path;
        }

        // Clean up any invalid result and record failure
        if (!string.IsNullOrEmpty(result?.Path))
        {
            try { File.Delete(result.Value.Path); } catch { }
        }

        // Record English download failure only if not cancelled
        if (!_cts.IsCancellationRequested)
        {
            _cache.RecordFailedDownload(appId, "english");
        }
        return null;
    }
    finally
    {
        _downloadSemaphore.Release();
    }
}
```

---

### 步驟 3：測試與驗證

#### 3.1 單元測試

**檔案位置：** `CommonUtilities.Tests/CdnLoadBalancerTests.cs`

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace CommonUtilities.Tests
{
    [TestClass]
    public class CdnLoadBalancerTests
    {
        [TestMethod]
        public void SelectBestCdn_ShouldReturnFirstWhenAllAvailable()
        {
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg",
                "https://shared.akamai.steamstatic.com/test.jpg"
            };

            var selected = balancer.SelectBestCdn(urls);

            // 應該選擇 CloudFlare（優先級最高）
            Assert.IsTrue(selected.Contains("cloudflare"));
        }

        [TestMethod]
        public void SelectBestCdn_ShouldAvoidBlockedDomain()
        {
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg"
            };

            // Block CloudFlare
            balancer.RecordBlockedDomain("shared.cloudflare.steamstatic.com");

            var selected = balancer.SelectBestCdn(urls);

            // 應該避開 CloudFlare，選擇 Steam CDN
            Assert.IsTrue(selected.Contains("cdn.steamstatic.com"));
        }

        [TestMethod]
        public void SelectBestCdn_ShouldSelectLeastBusyCdn()
        {
            var balancer = new CdnLoadBalancer(maxConcurrentPerDomain: 2);
            var urls = new List<string>
            {
                "https://shared.cloudflare.steamstatic.com/test.jpg",
                "https://cdn.steamstatic.com/test.jpg"
            };

            // Simulate CloudFlare being busy
            balancer.IncrementActiveRequests("shared.cloudflare.steamstatic.com");
            balancer.IncrementActiveRequests("shared.cloudflare.steamstatic.com");

            var selected = balancer.SelectBestCdn(urls);

            // 應該選擇較不繁忙的 Steam CDN
            Assert.IsTrue(selected.Contains("cdn.steamstatic.com"));
        }
    }
}
```

#### 3.2 整合測試

1. 清空圖片快取
2. 載入大量遊戲（100+）
3. 監控 DebugLogger 輸出：
   - 檢查是否有使用到多個 CDN
   - 確認負載分散是否合理
   - 驗證 429/403 錯誤處理

#### 3.3 監控指標

在 AnSAM 主視窗加入 CDN 統計顯示（可選）：

```csharp
// MainWindow.xaml.cs
private void ShowCdnStats()
{
    var stats = _imageService.GetCdnStats();
    var sb = new StringBuilder();
    sb.AppendLine("CDN Statistics:");
    foreach (var (domain, stat) in stats)
    {
        sb.AppendLine($"  {domain}:");
        sb.AppendLine($"    Active: {stat.Active}");
        sb.AppendLine($"    Blocked: {stat.IsBlocked}");
        sb.AppendLine($"    Success Rate: {stat.SuccessRate:P}");
    }
    DebugLogger.LogDebug(sb.ToString());
}
```

---

## 部署計畫

### 階段 1：開發與測試（Week 1-2）
- [ ] 實作 CdnLoadBalancer 類別
- [ ] 整合到 SharedImageService
- [ ] 撰寫單元測試
- [ ] 本地測試驗證

### 階段 2：Beta 測試（Week 3）
- [ ] 發布 Beta 版本
- [ ] 收集真實使用數據
- [ ] 監控 CDN 錯誤率
- [ ] 調整參數（block 時間、優先級等）

### 階段 3：正式發布（Week 4）
- [ ] 修正 Beta 測試發現的問題
- [ ] 更新文件
- [ ] 發布穩定版本

---

## 預期效果

### 效能提升
- **當前：** 2 個並發下載（全部使用 CloudFlare）
- **改進後：** 6 個並發下載（CloudFlare×2 + Steam×2 + Akamai×2）
- **速度提升：** 約 **3 倍**

### 穩定性提升
- 自動避開被 block 的 CDN
- 降低單一 CDN 過載風險
- 更好的錯誤恢復能力

### 使用者體驗
- 更快的圖片載入速度
- 減少等待時間
- 更流暢的語言切換

---

## 維護建議

### 定期檢查
- 每月檢查 CDN 統計數據
- 評估各 CDN 的成功率
- 根據數據調整優先級

### 參數調整
- 根據實際情況調整 block 時間（當前 5 分鐘）
- 可能需要調整 maxConcurrentPerDomain（當前 2）
- 監控並調整 CDN 優先級

### 未來優化
- 加入使用者可配置的「品質優先」vs「速度優先」模式
- 實作更精細的 CDN 品質評分系統
- 考慮加入 CDN 回應時間追蹤
