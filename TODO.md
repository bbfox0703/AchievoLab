# AchievoLab - To-Do List

## 最近更新 (2026-01-01)

### ✅ 已完成項目

#### 1. ✅ BitmapImage 記憶體洩漏修正
**位置：** `AnSAM/MainWindow.xaml.cs:942-967`
**修正：** 在 CoverPath setter 中清除舊 BitmapImage 參照
**狀態：** 已完成

#### 2. ✅ CDN 負載均衡器實作
**位置：** `CommonUtilities/CdnLoadBalancer.cs` (新檔案)
**功能：**
- 智能 CDN 選擇（CloudFlare > Steam > Akamai）
- 自動追蹤每個 CDN 的並發數和成功率
- 429/403 自動阻擋機制（5 分鐘）
- 選擇最少忙碌的 CDN
**測試：** 10 個單元測試全部通過
**狀態：** 已完成並整合到 SharedImageService

#### 3. ✅ HTTP 429/403 監控與處理
**位置：** `SharedImageService.cs:593-602`, `GameImageCache.cs:402-406`
**功能：**
- 偵測 HTTP 429 (Too Many Requests) 和 403 (Forbidden)
- 自動將該 CDN 標記為已阻擋（5 分鐘）
- 切換到其他可用 CDN
**狀態：** 已完成

#### 4. ✅ 語系圖片快取重複修正
**位置：** `CommonUtilities/GameImageCache.cs`
**修正：**
- 移除英文圖片複製到語系資料夾的機制
- 新增 `CleanupDuplicatedEnglishImages()` 清理方法
- 修正重試機制失效問題
**預期效果：** 節省 100-500 MB 磁碟空間
**文件：** `docs/LANGUAGE_CACHE_FIX_SUMMARY.md`
**狀態：** 已完成

#### 5. ✅ AnSAM 啟動時執行快取清理
**位置：** `AnSAM/MainWindow.xaml.cs:366-380`
**功能：** 首次啟動時在背景執行 CleanupDuplicatedEnglishImages
**狀態：** 已完成

#### 6. ✅ CDN 統計顯示
**位置：** `AnSAM/MainWindow.xaml`, `AnSAM/MainWindow.xaml.cs:410-464`
**功能：**
- 每 2 秒更新 CDN 統計資料
- 顯示格式：`CDN: CF:4 Steam:2 (98%)`
- 顯示阻擋警告：`CF:2⚠`
- 顯示整體成功率
**狀態：** 已完成

#### 7. ✅ 關鍵字搜尋欄位加寬
**位置：** `AnSAM/MainWindow.xaml:21`
**修改：** Width 從 280 → 336 (+20%)
**狀態：** 已完成

#### 8. ✅ CDN 並發限制調整
**修改：**
- CdnLoadBalancer: `maxConcurrentPerDomain: 2 → 4`
- DomainRateLimiter: `maxConcurrentRequestsPerDomain: 2 → 4`
**效果：** 理論上 3 個 CDN × 4 並發 = 最多 12 個同時下載
**狀態：** 已完成

---

## 🔴 高優先級 - 待修正問題

### 1. ⚠️ CdnLoadBalancer 競態條件
**問題：** CDN 選擇和計數遞增之間存在競態條件
**現象：** 可能看到單一 CDN 顯示超過限制數字（例如顯示 7 但限制是 4）
**影響：**
- 顯示數字不準確（短暫峰值）
- DomainRateLimiter 仍會正確限制實際連線數

**解決方案選項：**
```csharp
// 選項 A: 在 CdnLoadBalancer 中加入 Semaphore（複雜）
private readonly ConcurrentDictionary<string, SemaphoreSlim> _domainSemaphores;

// 選項 B: 加入 lock 確保選擇+遞增的原子性（簡單）
private readonly object _selectionLock = new();
public string SelectBestCdn(List<string> cdnUrls)
{
    lock (_selectionLock)
    {
        // ... 選擇邏輯 ...
        IncrementActiveRequests(selectedDomain);
        return selectedUrl;
    }
}

// 選項 C: 接受競態條件，僅用於監控（當前狀態）
// DomainRateLimiter 已經在 GameImageCache 層正確限制
```

**建議：** 選項 C（監控用途）或選項 B（精確計數）
**優先級：** 中 - 不影響實際功能，僅影響顯示準確性

---

## 🟡 中優先級 - 效能優化

### 2. SharedImageService._pendingRequests 自動清理
**位置：** `CommonUtilities/SharedImageService.cs:93-105`
**問題：** `CleanupStaleRequests()` 方法存在但從未被自動調用
**影響：** 長時間運行後，字典中可能累積已完成的 Task
**建議實作：**
```csharp
public async Task<string?> GetGameImageAsync(int appId, string language)
{
    // 定期清理（例如每 100 次請求）
    if (_requestCount++ % 100 == 0)
    {
        CleanupStaleRequests();
    }
    // ...
}
```
**優先級：** 中

### 3. 語言切換批次大小優化
**位置：** `AnSAM/MainWindow.xaml.cs:845`
**當前：** `batchSize = 3, delay = 30ms`
**建議：**
- `batchSize = 5-8` (從 3 增加到 5-8)
- `delay = 20ms` (從 30ms 減少到 20ms)
**理由：** 現在有 CDN 負載均衡，可以更激進地批次載入
**優先級：** 中

### 4. 增加 SharedImageService 最大並發數
**位置：** `SharedImageService.cs:26`
**當前：** `MAX_CONCURRENT_DOWNLOADS = 10`
**建議：** 增加到 15-20
**理由：**
- 現在有 3 個 CDN，每個限制 4 並發 = 理論上 12 個
- 加上等待和切換，15-20 個 slot 更合理
**風險：** 需要監控 HTTP 429/403 錯誤率
**優先級：** 中

---

## 🟢 低優先級 - 未來改進

### 5. CDN 優先級動態調整
**構想：** 根據成功率動態調整 CDN 優先級
```csharp
// 如果 CloudFlare 成功率 < 80%，降低優先級
// 如果 Steam CDN 成功率 > 95%，提升優先級
```
**優先級：** 低

### 6. 圖片快取過期策略改進
**當前：** 固定 30 天 TTL
**建議：**
- 熱門遊戲圖片：60 天
- 冷門遊戲圖片：15 天
- 根據存取頻率動態調整
**優先級：** 低

### 7. 支援 WebP 格式
**位置：** `GameImageCache.cs:399`
**當前：** 已經在 Accept header 中包含 WebP
**改進：** 主動偵測並優先使用 WebP（檔案更小）
**優先級：** 低

### 8. 並發下載進度顯示
**構想：** 在狀態列顯示「正在下載：8/12」
**位置：** `MainWindow.xaml.cs` StatusExtra
**優先級：** 低

---

## 測試計畫

### ✅ 已完成測試
- [x] CDN 負載均衡器單元測試（10 個測試全部通過）
- [x] 編譯測試（0 警告，0 錯誤）

### 🔲 待執行測試

#### 整合測試
1. **CDN 切換測試**
   - 模擬 CloudFlare 被阻擋，確認切換到 Steam CDN
   - 5 分鐘後確認 CloudFlare 解除阻擋

2. **並發壓力測試**
   - 清空快取
   - 載入 100+ 遊戲
   - 監控：
     - CDN 統計顯示是否正確
     - 實際並發數是否符合限制
     - 429/403 錯誤率

3. **語言切換測試**
   - 清空快取
   - 切換語言：英文 → 繁中 → 日文 → 韓文
   - 確認：
     - 圖片正確顯示
     - 沒有重複下載
     - 清理功能正常運作

4. **快取清理測試**
   - 檢查啟動 log，確認清理執行
   - 手動檢查語系資料夾，確認重複檔案已移除
   - 計算節省的磁碟空間

#### 記憶體測試
1. **長時間運行測試**
   - 運行 AnSAM 1 小時
   - 重複切換語言、滾動遊戲列表
   - 使用 Visual Studio Memory Profiler 檢查記憶體趨勢

2. **BitmapImage 洩漏驗證**
   - 切換語言 20 次
   - 確認舊的 BitmapImage 被 GC 回收

---

## 效能基準

### 理論值
- **最大並發下載：** 12 個（3 CDN × 4）
- **單一 CDN 限制：** 4 個
- **整體並發限制：** 10 個（SharedImageService）← **建議增加**

### 實際觀測值（待測試）
- **首次載入 100 遊戲耗時：** _待測量_
- **語言切換耗時：** _待測量_
- **CDN 分布：** _待觀測_
- **429/403 錯誤率：** _待監控_

---

## 文件更新記錄

### 新增文件
- `CLAUDE.md` - Claude Code 指南
- `docs/CONCURRENCY_ANALYSIS.md` - 並發問題分析
- `docs/CDN_FAILOVER_IMPLEMENTATION.md` - CDN 切換實作指南
- `docs/LANGUAGE_IMAGE_CACHE_ANALYSIS.md` - 語系快取問題分析
- `docs/LANGUAGE_CACHE_FIX_SUMMARY.md` - 語系快取修正總結

### 新增程式碼
- `CommonUtilities/CdnLoadBalancer.cs` - CDN 負載均衡器
- `CommonUtilities.Tests/CdnLoadBalancerTests.cs` - 單元測試

---

## 版本歷史

### 2026-01-01 (當前)
- ✅ 修正 BitmapImage 記憶體洩漏
- ✅ 實作 CDN 負載均衡器
- ✅ 新增 HTTP 429/403 處理機制
- ✅ 修正語系圖片快取重複問題
- ✅ 新增啟動時自動清理功能
- ✅ 新增 CDN 統計即時顯示
- ✅ 調整 CDN 並發限制 (2→4)
- ✅ 加寬關鍵字搜尋欄位 (+20%)

---

## 下次工作建議

### 優先順序排序

**立即執行（本週）：**
1. 執行並發壓力測試，驗證 CDN 負載均衡效果
2. 執行快取清理測試，確認磁碟空間節省
3. 監控 429/403 錯誤率

**短期（1-2 週）：**
1. 增加 SharedImageService MAX_CONCURRENT_DOWNLOADS (10→15)
2. 實作 _pendingRequests 自動清理
3. 優化語言切換批次大小

**長期（1 個月+）：**
1. 修正 CdnLoadBalancer 競態條件（如果影響嚴重）
2. 實作動態 CDN 優先級調整
3. 改進快取過期策略

---

## 聯絡與反饋

如發現問題或有改進建議，請：
1. 檢查 DebugLogger 輸出
2. 查看 `docs/` 目錄中的相關分析文件
3. 參考本 TODO.md 的測試計畫
