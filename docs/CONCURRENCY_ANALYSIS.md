# 並發下載與 CDN 策略分析報告

## 當前配置分析

### 1. 並發控制層級

| 層級 | 位置 | 限制值 | 範圍 |
|------|------|--------|------|
| Level 1 | SharedImageService._downloadSemaphore | 10 | 全域 |
| Level 2 | GameImageCache._concurrency | 4 | 每個 GameImageCache 實例 |
| Level 3 | DomainRateLimiter (per-domain) | 2 | 每個域名 |

### 2. 使用的 CDN 域名

```
1. shared.cloudflare.steamstatic.com  (CloudFlare)
2. cdn.steamstatic.com                 (Steam CDN)
3. shared.akamai.steamstatic.com       (Akamai)
```

### 3. 當前下載流程

```
for each image:
    urls = RoundRobin([CloudFlare, Steam, Akamai])
    for each url in urls:
        try:
            wait for DomainRateLimiter (max 2 per domain)
            download from url
            if success: break
        catch:
            try next url
```

**問題：** 即使 CloudFlare 已經有 2 個並發連線，其他圖片也會等待，而不是立即嘗試 Steam CDN 或 Akamai。

---

## 風險評估：增加 maxConcurrency 到 8-10

### 場景 1：所有圖片都成功從 CloudFlare 下載

**當前 (maxConcurrency=4):**
- GameImageCache: 最多 4 個並發下載
- DomainRateLimiter: CloudFlare 最多 2 個並發
- 實際並發數：**2 個**（受限於 DomainRateLimiter）
- 其他 2 個 slot 閒置等待

**調整後 (maxConcurrency=10):**
- GameImageCache: 最多 10 個並發下載
- DomainRateLimiter: CloudFlare 最多 2 個並發
- 實際並發數：**仍然 2 個**（受限於 DomainRateLimiter）
- 其他 8 個 slot 閒置等待

**結論：** 增加 maxConcurrency **不會改善效能**，因為瓶頸在 DomainRateLimiter。

### 場景 2：CloudFlare 開始返回 429 (Too Many Requests)

**當前流程：**
```
Image 1: CloudFlare (slot 1)
Image 2: CloudFlare (slot 2)
Image 3: 等待 CloudFlare 釋放 → CloudFlare 返回 429 → 嘗試 Steam CDN → 成功
Image 4: 等待 CloudFlare 釋放 → CloudFlare 返回 429 → 嘗試 Steam CDN → 成功
```

**問題：** 每個圖片都要先等待 CloudFlare，失敗後才嘗試其他 CDN，造成延遲。

### 場景 3：分散到多個 CDN

**理想情況：**
```
Image 1: CloudFlare (slot 1)
Image 2: CloudFlare (slot 2)
Image 3: Steam CDN (slot 1) ← 直接使用，不等待 CloudFlare
Image 4: Steam CDN (slot 2)
Image 5: Akamai (slot 1)
Image 6: Akamai (slot 2)
Total: 6 個並發下載
```

**當前實作無法達成：** 因為每個圖片都會按 RoundRobin 順序嘗試，不會主動跳過已滿的 CDN。

---

## 建議方案

### ❌ 方案 A：單純增加 maxConcurrency（不推薦）

**調整：** GameImageCache.maxConcurrency 從 4 增加到 8-10

**優點：** 無

**缺點：**
- 不會改善效能（瓶頸在 DomainRateLimiter）
- 增加記憶體消耗
- 更多執行緒等待

**結論：** **不建議單獨採用此方案**

---

### ✅ 方案 B：實作智能 CDN 選擇器（強烈推薦）

**核心概念：** 在發起下載前，動態選擇當前最不繁忙的 CDN。

#### 實作設計

```csharp
/// <summary>
/// 智能 CDN 負載均衡器，根據當前並發數和失敗記錄選擇最佳 CDN
/// </summary>
public class CdnLoadBalancer
{
    private readonly DomainRateLimiter _rateLimiter;
    private readonly ConcurrentDictionary<string, int> _activeRequests = new();
    private readonly ConcurrentDictionary<string, DateTime> _blockedUntil = new();

    public string SelectBestCdn(List<string> cdnUrls, int maxPerDomain = 2)
    {
        var available = cdnUrls
            .Select(url => new { Url = url, Domain = new Uri(url).Host })
            .Where(x =>
            {
                // 過濾掉被 block 的 CDN（5 分鐘內）
                if (_blockedUntil.TryGetValue(x.Domain, out var blockedTime))
                {
                    if (DateTime.UtcNow < blockedTime)
                        return false;
                }

                // 檢查當前並發數
                var activeCount = _activeRequests.GetOrAdd(x.Domain, 0);
                return activeCount < maxPerDomain;
            })
            .OrderBy(x => _activeRequests.GetOrAdd(x.Domain, 0)) // 選擇並發數最少的
            .ThenBy(x => Guid.NewGuid()) // 相同並發數時隨機選擇
            .FirstOrDefault();

        return available?.Url ?? cdnUrls.First(); // Fallback 到第一個
    }

    public void RecordBlockedDomain(string domain, TimeSpan duration)
    {
        _blockedUntil[domain] = DateTime.UtcNow.Add(duration);
    }

    public void IncrementActiveRequests(string domain)
    {
        _activeRequests.AddOrUpdate(domain, 1, (_, count) => count + 1);
    }

    public void DecrementActiveRequests(string domain)
    {
        _activeRequests.AddOrUpdate(domain, 0, (_, count) => Math.Max(0, count - 1));
    }
}
```

#### 整合到 SharedImageService

**修改位置：** `SharedImageService.TryDownloadLanguageSpecificImageAsync()`

**修改前：**
```csharp
var languageUrls = RoundRobin(languageSpecificUrlMap);
var result = await _cache.GetImagePathAsync(appId.ToString(), languageUrls, ...);
```

**修改後：**
```csharp
var languageUrls = RoundRobin(languageSpecificUrlMap);

// 選擇最佳 CDN URL
var bestUrl = _cdnLoadBalancer.SelectBestCdn(languageUrls);

// 只嘗試選中的 URL，如果失敗再嘗試其他
var result = await TryDownloadWithFallback(appId, bestUrl, languageUrls, language);
```

**新增方法：**
```csharp
private async Task<ImageResult?> TryDownloadWithFallback(
    int appId,
    string primaryUrl,
    List<string> fallbackUrls,
    string language)
{
    var urlsToTry = new List<string> { primaryUrl };
    urlsToTry.AddRange(fallbackUrls.Where(url => url != primaryUrl));

    foreach (var url in urlsToTry)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            continue;

        var domain = uri.Host;
        _cdnLoadBalancer.IncrementActiveRequests(domain);

        try
        {
            var result = await _cache.GetImagePathAsync(
                appId.ToString(),
                uri,
                language,
                appId,
                _cts.Token);

            if (!string.IsNullOrEmpty(result.Path))
            {
                return result; // 成功
            }
        }
        catch (HttpRequestException ex) when (
            ex.Message.Contains("429") ||
            ex.Message.Contains("403"))
        {
            // CDN 返回限速錯誤，標記為 blocked
            _cdnLoadBalancer.RecordBlockedDomain(domain, TimeSpan.FromMinutes(5));
            DebugLogger.LogDebug($"CDN {domain} blocked, trying next...");
        }
        finally
        {
            _cdnLoadBalancer.DecrementActiveRequests(domain);
        }
    }

    return null; // 所有 CDN 都失敗
}
```

#### 優點
1. **顯著提升速度：** 充分利用所有 CDN 的並發容量
   - CloudFlare: 2 並發
   - Steam CDN: 2 並發
   - Akamai: 2 並發
   - **總計：6 個並發下載**（當前只有 2 個）

2. **智能容錯：** 自動避開被 block 的 CDN

3. **負載均衡：** 均勻分散請求到各 CDN

4. **無需調整 maxConcurrency：** 保持 4 即可充分利用

#### 缺點
1. **圖片品質不一致：** 不同 CDN 可能提供不同品質的圖片
   - **緩解：** 優先使用 CloudFlare，只有在其繁忙時才使用其他 CDN

2. **複雜度增加：** 需要維護 CDN 狀態

3. **初次載入可能出現混合來源：** 部分圖片來自 CloudFlare，部分來自 Akamai
   - **緩解：** 快取後下次載入都來自相同來源

---

### ⚖️ 方案 C：混合方案（平衡）

**組合：**
1. 實作智能 CDN 選擇器（方案 B）
2. 小幅增加 maxConcurrency 到 6-8

**理由：**
- 當前 maxConcurrency=4，但有 3 個 CDN × 2 並發 = 6 個可用 slot
- 增加到 6-8 可充分利用所有 CDN
- 不要增加太多（避免過度消耗資源）

**建議配置：**
```csharp
// SharedImageService
private const int MAX_CONCURRENT_DOWNLOADS = 10; // 保持不變

// GameImageCache
maxConcurrency = 6  // 從 4 增加到 6

// DomainRateLimiter
maxConcurrentRequestsPerDomain = 2 // 保持不變（安全值）
```

---

## 實作優先順序

### 階段 1：核心 CDN 選擇器（必須）
1. 實作 `CdnLoadBalancer` 類別
2. 整合到 `SharedImageService`
3. 測試基本功能

### 階段 2：智能 Block 檢測（重要）
1. 檢測 HTTP 429/403 回應
2. 記錄被 block 的 CDN 和恢復時間
3. 自動繞過被 block 的 CDN

### 階段 3：監控與調優（建議）
1. 加入 DebugLogger 輸出 CDN 使用統計
2. 記錄每個 CDN 的成功率
3. 根據統計數據調整策略

### 階段 4：併發數調整（可選）
1. 在完成階段 1-3 後進行壓力測試
2. 根據測試結果決定是否增加 maxConcurrency
3. 監控 CDN 錯誤率

---

## 測試計畫

### 測試環境
- 準備 200+ 遊戲的測試帳號
- 清空圖片快取
- 使用網路監控工具（如 Fiddler）

### 測試案例

#### Case 1：正常負載測試
- 載入 200 個遊戲
- 監控：
  - 各 CDN 的請求分布
  - 平均下載時間
  - 429/403 錯誤數

#### Case 2：高負載壓力測試
- 快速滾動遊戲列表（觸發大量下載）
- 切換語言（重新下載所有圖片）
- 監控：
  - CDN 是否被 block
  - 自動 failover 是否生效
  - 整體下載完成時間

#### Case 3：CDN 故障模擬
- 手動 block CloudFlare（透過 hosts 或防火牆）
- 驗證自動切換到 Steam CDN/Akamai
- 恢復 CloudFlare，驗證重新使用

---

## 效能預估

### 當前效能（maxConcurrency=4, 無 CDN 負載均衡）
- 實際並發數：**2**（全部使用 CloudFlare）
- 下載 100 張圖片預估時間：**50 秒**
  - 假設每張圖片 1 秒下載 = 100/2 = 50 秒

### 方案 B 實作後（保持 maxConcurrency=4）
- 實際並發數：**6**（CloudFlare×2 + Steam×2 + Akamai×2）
- 下載 100 張圖片預估時間：**17 秒**
  - 100/6 ≈ 17 秒
  - **速度提升：3 倍**

### 方案 C 實作後（maxConcurrency=6 + CDN 負載均衡）
- 實際並發數：**6**（完全利用）
- 下載 100 張圖片預估時間：**17 秒**
  - 與方案 B 相同（因為瓶頸在 DomainRateLimiter）
  - 但更有餘裕處理快取命中/失敗的情況

---

## 建議決策

### ✅ 立即實作：方案 B（智能 CDN 選擇器）
**原因：**
- 最大效能提升（3 倍速度）
- 不需調整並發數（風險低）
- 充分利用現有資源

### ⏸️ 暫緩實作：單純增加 maxConcurrency
**原因：**
- 無效能提升
- 增加資源消耗
- 等方案 B 完成後再評估

### 🔄 後續評估：方案 C（混合方案）
**時機：** 方案 B 上線並穩定運行 1-2 週後
**條件：** 如果測試顯示仍有效能瓶頸

---

## 風險控管

### 主要風險：圖片品質不一致

**問題：** 不同 CDN 可能提供不同解析度或壓縮率的圖片

**緩解策略：**
1. **優先級排序：** CloudFlare > Steam CDN > Akamai
   ```csharp
   var cdnPriority = new Dictionary<string, int>
   {
       ["shared.cloudflare.steamstatic.com"] = 1,
       ["cdn.steamstatic.com"] = 2,
       ["shared.akamai.steamstatic.com"] = 3
   };
   ```

2. **快取鎖定：** 一旦成功下載並快取，後續永遠使用相同來源
   - 在快取檔案中記錄來源 CDN
   - 下次優先從相同 CDN 下載

3. **使用者選項：** 提供「優先品質」vs「優先速度」設定
   - 優先品質：只使用 CloudFlare（當前行為）
   - 優先速度：使用負載均衡（新功能）

### 次要風險：CDN Block

**問題：** 過度使用可能導致所有 CDN 都被 block

**緩解策略：**
1. 保守的 DomainRateLimiter 設定（每 CDN 2 並發）
2. 尊重 HTTP 429 Retry-After header
3. 指數退避（已實作於 DomainRateLimiter）
4. 總並發數限制（SharedImageService = 10）

---

## 結論

**推薦方案：** 方案 B（智能 CDN 選擇器）

**實作路徑：**
1. Week 1: 實作 CdnLoadBalancer 核心功能
2. Week 2: 整合到 SharedImageService 並測試
3. Week 3: 加入監控與調優
4. Week 4: 根據數據決定是否調整 maxConcurrency

**預期效果：**
- 下載速度提升 **2-3 倍**
- 更好的容錯能力
- 無需增加單一 CDN 的請求頻率（避免被 block）
