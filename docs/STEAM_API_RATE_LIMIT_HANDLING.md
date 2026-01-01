# Steam API 429 Rate Limit 處理機制

## 實作日期
2026-01-01

---

## 📋 實作摘要

為 `SteamApiService` 新增 HTTP 429 (Too Many Requests) 自動偵測與阻擋機制，當 Steam API 回應 429 時，自動暫停所有 API 呼叫 **30 分鐘**。

---

## 🎯 問題背景

### 原有機制的問題
**檔案：** `MyOwnGames/SteamApiService.cs` (修改前)

**問題 1：僅等待 75 秒**
```csharp
catch (HttpRequestException ex) when (ex.Message.Contains("429"))
{
    // 只等待 75 秒 - 對 Steam API 來說太短！
    await Task.Delay(TimeSpan.FromSeconds(75), cancellationToken);
}
```

**問題 2：無全域阻擋機制**
- 每個方法獨立處理 429
- 無法防止其他並行請求繼續呼叫 API
- 可能持續觸發 429，導致更長的封鎖時間

**問題 3：使用 GetStringAsync 無法檢查狀態碼**
```csharp
var response = await _httpClient.GetStringAsync(url, cancellationToken);
// 無法檢查 response.StatusCode，只能靠 exception message
```

---

## ✅ 新實作機制

### 1. 全域阻擋狀態追蹤

**新增欄位：**
```csharp
// Steam API rate limit tracking
private DateTime? _steamApiBlockedUntil = null;
private readonly object _blockLock = new();
```

**阻擋檢查方法：**
```csharp
private bool IsSteamApiBlocked()
{
    lock (_blockLock)
    {
        if (_steamApiBlockedUntil.HasValue)
        {
            if (DateTime.UtcNow < _steamApiBlockedUntil.Value)
            {
                var timeRemaining = _steamApiBlockedUntil.Value - DateTime.UtcNow;
                DebugLogger.LogDebug($"Steam API is blocked for {timeRemaining.TotalMinutes:F1} more minutes");
                return true;
            }
            else
            {
                // Block has expired
                _steamApiBlockedUntil = null;
                DebugLogger.LogDebug("Steam API block has expired");
            }
        }
        return false;
    }
}
```

**阻擋記錄方法：**
```csharp
private void RecordSteamApiRateLimit()
{
    lock (_blockLock)
    {
        _steamApiBlockedUntil = DateTime.UtcNow.AddMinutes(30);
        DebugLogger.LogDebug($"Steam API blocked until {_steamApiBlockedUntil.Value:HH:mm:ss} (30 minutes)");
    }
}
```

### 2. 安全的 HTTP GET 方法

**新增方法：**
```csharp
private async Task<string> GetStringWithRateLimitCheckAsync(string url, CancellationToken cancellationToken = default)
{
    using var response = await _httpClient.GetAsync(url, cancellationToken);

    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
    {
        RecordSteamApiRateLimit();
        throw new HttpRequestException($"Steam API rate limit exceeded (429). Blocked for 30 minutes.");
    }

    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync(cancellationToken);
}
```

**優點：**
- ✅ 使用 `GetAsync` 可檢查 HTTP 狀態碼
- ✅ 精確偵測 429 回應
- ✅ 自動記錄阻擋狀態
- ✅ 拋出明確的錯誤訊息

### 3. 修改現有 API 方法

#### GetOwnedGamesAsync
**位置：** `SteamApiService.cs:62-150`

**新增阻擋檢查：**
```csharp
public async Task<int> GetOwnedGamesAsync(...)
{
    ValidateCredentials(_apiKey, steamId64);

    // ✅ 新增：檢查是否被阻擋
    if (IsSteamApiBlocked())
    {
        throw new InvalidOperationException("Steam API is currently blocked due to rate limiting. Please wait 30 minutes before trying again.");
    }

    try
    {
        // ...
        // ✅ 修改：使用安全方法
        var ownedGamesResponse = await GetStringWithRateLimitCheckAsync(ownedGamesUrl, cancellationToken);
        // ...
    }
}
```

#### GetLocalizedGameNameAsync
**位置：** `SteamApiService.cs:153-197`

**修改內容：**
```csharp
// ✅ 修改：使用安全方法
var response = await GetStringWithRateLimitCheckAsync(url, cancellationToken);

// ✅ 修改：更新 429 處理邏輯
catch (HttpRequestException ex) when (ex.Message.Contains("429") ||
                                       ex.Message.Contains("Too Many Requests") ||
                                       ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
{
    // 429 already recorded by GetStringWithRateLimitCheckAsync, Steam API blocked for 30 minutes
    DebugLogger.LogDebug($"Rate limited when getting localized name for {appId}, using English fallback. Steam API blocked for 30 minutes.");
}
```

---

## 🔄 運作流程

### 正常情況
```
1. 呼叫 GetOwnedGamesAsync()
2. IsSteamApiBlocked() → false (未被阻擋)
3. GetStringWithRateLimitCheckAsync(url)
4. HTTP GET → 200 OK
5. 返回資料 ✅
```

### 收到 429 的情況
```
1. 呼叫 GetOwnedGamesAsync()
2. IsSteamApiBlocked() → false (未被阻擋)
3. GetStringWithRateLimitCheckAsync(url)
4. HTTP GET → 429 Too Many Requests ⚠️
5. RecordSteamApiRateLimit() → 記錄阻擋到 30 分鐘後
6. 拋出 HttpRequestException
7. UI 顯示錯誤訊息給使用者
```

### 被阻擋期間的情況
```
1. 呼叫 GetOwnedGamesAsync()
2. IsSteamApiBlocked() → true (剩餘 25.3 分鐘) ⚠️
3. 立即拋出 InvalidOperationException
4. 完全不發送 HTTP 請求
5. UI 顯示「請等待 30 分鐘」
```

### 阻擋期滿的情況
```
1. 呼叫 GetOwnedGamesAsync()
2. IsSteamApiBlocked() → 檢查時間已過期
   - _steamApiBlockedUntil = null
   - 返回 false
3. 恢復正常 API 呼叫 ✅
```

---

## 🆚 與 CDN 429 處理的差異

| 項目 | CDN (圖片下載) | Steam API (遊戲列表) |
|------|---------------|---------------------|
| **實作位置** | `CdnLoadBalancer.cs` | `SteamApiService.cs` |
| **阻擋時間** | 5 分鐘 | **30 分鐘** |
| **阻擋範圍** | 單一 CDN 域名 | **整個 Steam API** |
| **替代方案** | 切換到其他 CDN | ❌ 無替代方案 |
| **影響** | 圖片載入速度 | **完全無法掃描遊戲** |
| **嚴重性** | 低（有備援） | **高（需等待恢復）** |

**為什麼 Steam API 需要更長的阻擋時間？**
1. Steam API 有更嚴格的限流政策
2. 無法像 CDN 一樣切換到其他服務
3. 過早重試可能導致更長的封鎖（甚至 API Key 被暫停）
4. 使用者通常一次性掃描大量遊戲，429 風險更高

---

## 📊 使用情境

### 情境 1：首次掃描大量遊戲
**操作：** 使用者輸入 Steam ID，點擊「Get Games」

**可能觸發 429 的情況：**
- 遊戲數量 > 500
- 選擇非英文語言（需額外呼叫 appdetails API）
- 短時間內多次重試

**系統行為：**
```
[2026-01-01 10:00:00] Retrieving game 234/800...
[2026-01-01 10:00:05] Rate limited when getting localized name for 12345, using English fallback. Steam API blocked for 30 minutes.
[2026-01-01 10:00:05] Steam API blocked until 10:30:00 (30 minutes)
[2026-01-01 10:00:05] Retrieving game 235/800 (using English name)
... 繼續處理，但使用英文名稱 fallback
```

### 情境 2：阻擋期間嘗試重新掃描
**操作：** 10:10 時再次點擊「Get Games」

**系統行為：**
```
[2026-01-01 10:10:00] Steam API is blocked for 20.0 more minutes
[錯誤對話框] Steam API is currently blocked due to rate limiting. Please wait 30 minutes before trying again.
```

### 情境 3：阻擋期滿後恢復
**操作：** 10:30 後再次點擊「Get Games」

**系統行為：**
```
[2026-01-01 10:30:05] Steam API block has expired
[2026-01-01 10:30:05] Starting complete game scan...
[正常執行] ✅
```

---

## 🧪 測試建議

### 單元測試 (可選)
```csharp
[Fact]
public void IsSteamApiBlocked_ReturnsTrueWhenBlocked()
{
    var service = new SteamApiService("test_key");

    // Simulate 429 response
    service.RecordSteamApiRateLimit();

    // Should be blocked
    Assert.True(service.IsSteamApiBlocked());
}

[Fact]
public void IsSteamApiBlocked_ReturnsFalseAfter30Minutes()
{
    var service = new SteamApiService("test_key");
    service.RecordSteamApiRateLimit();

    // Simulate 30 minutes passing
    Thread.Sleep(TimeSpan.FromMinutes(30).Add(TimeSpan.FromSeconds(1)));

    // Should no longer be blocked
    Assert.False(service.IsSteamApiBlocked());
}
```

### 整合測試
1. **手動觸發 429 測試**（需要實際 API）
   - 快速連續呼叫 API 直到收到 429
   - 確認系統記錄 30 分鐘阻擋
   - 確認後續呼叫被拒絕

2. **UI 錯誤訊息測試**
   - 確認使用者看到明確的錯誤訊息
   - 訊息包含等待時間資訊
   - 提供建議（等待 30 分鐘）

---

## 📝 DebugLogger 輸出範例

### 正常運作
```
[2026-01-01 10:00:00] Fetching localized name for 570 (Dota 2) in tchinese
[2026-01-01 10:00:01] Got localized name for 570: 'Dota 2'
```

### 收到 429
```
[2026-01-01 10:05:23] Fetching localized name for 730 (Counter-Strike 2) in tchinese
[2026-01-01 10:05:24] Steam API blocked until 10:35:24 (30 minutes)
[2026-01-01 10:05:24] Rate limited when getting localized name for 730, using English fallback. Steam API blocked for 30 minutes.
```

### 阻擋期間嘗試呼叫
```
[2026-01-01 10:15:00] Steam API is blocked for 20.4 more minutes
[錯誤] InvalidOperationException: Steam API is currently blocked due to rate limiting. Please wait 30 minutes before trying again.
```

### 阻擋期滿
```
[2026-01-01 10:35:30] Steam API block has expired
[2026-01-01 10:35:30] Starting complete game scan for language: tchinese...
```

---

## ⚠️ 注意事項

### 1. 與圖片下載無關
**重要：** 此機制**僅影響 Steam API 呼叫**，不影響圖片下載
- 圖片下載使用 CDN (steamstatic.com)
- CDN 有自己的 5 分鐘阻擋機制
- 兩者完全獨立

### 2. 30 分鐘是建議值
**可調整參數：**
```csharp
_steamApiBlockedUntil = DateTime.UtcNow.AddMinutes(30); // 可改為 15, 45, 60 等
```

**建議：**
- 首次觸發：30 分鐘
- 頻繁觸發：考慮增加到 60 分鐘
- 測試環境：可減少到 5 分鐘

### 3. 不影響現有快取資料
- 已掃描的遊戲列表會保留
- 本地化名稱快取仍可使用
- 圖片快取不受影響

### 4. 跨實例阻擋
**問題：** 如果同時運行多個 MyOwnGames 實例？

**現狀：** 每個實例獨立追蹤阻擋狀態

**未來改進（可選）：**
- 使用檔案鎖記錄全域阻擋狀態
- 所有實例共享阻擋資訊

---

## 🚀 部署檢查清單

- [x] 新增 `_steamApiBlockedUntil` 和 `_blockLock` 欄位
- [x] 實作 `IsSteamApiBlocked()` 方法
- [x] 實作 `RecordSteamApiRateLimit()` 方法
- [x] 實作 `GetStringWithRateLimitCheckAsync()` 方法
- [x] 修改 `GetOwnedGamesAsync()` 加入阻擋檢查
- [x] 修改 `GetLocalizedGameNameAsync()` 使用安全方法
- [x] 更新 429 exception 處理邏輯
- [x] 編譯測試（0 錯誤，0 警告）
- [ ] 實際測試（觸發 429 並驗證阻擋機制）
- [ ] 更新使用者文件（如需要）

---

## 📚 相關文件

- `docs/CDN_FAILOVER_IMPLEMENTATION.md` - CDN 負載均衡機制
- `TODO.md` - 待辦事項清單
- `CLAUDE.md` - 開發指南

---

**實作完成日期：** 2026-01-01
**編譯狀態：** ✅ 成功（0 警告，0 錯誤）
**測試狀態：** ⏳ 待實際測試
