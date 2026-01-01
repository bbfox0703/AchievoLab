# AchievoLab - To-Do List

## 效能優化與記憶體洩漏修正 (2026-01-01)

### 🔴 高優先級 - 需要立即修正

#### 1. ✅ BitmapImage 記憶體洩漏 (COMPLETED)
**位置：** `AnSAM/MainWindow.xaml.cs` GameItem 類別
**問題：** 當 CoverPath 改變時，舊的 BitmapImage 沒有被 Dispose
**狀態：** 已修正
**修正內容：** 在 CoverPath setter 中加入舊圖片的 Dispose 邏輯

---

### 🟡 中優先級 - 效能優化評估

#### 2. ⏸️ GameImageCache 並發數調整評估
**位置：** `CommonUtilities/GameImageCache.cs:50`
**當前設定：** `maxConcurrency = 4`
**建議調整：** 增加到 8-10
**風險評估：**
- **高風險場景：** 所有圖片來自同一個 CDN（例如全部從 CloudFlare）
  - 當前 DomainRateLimiter 限制每域名 2 個並發
  - 即使增加到 10，實際仍受域名限制影響
  - 可能觸發 CDN 的 429 Too Many Requests 或 403 Forbidden

- **緩解策略：**
  1. 保持 GameImageCache maxConcurrency = 4-6（較保守）
  2. 改善 DomainRateLimiter 策略（見下方）
  3. 監控並處理 HTTP 429/403 回應

**建議：** 先實作 CDN Failover 策略（任務 3），再考慮增加並發數

---

#### 3. ⏸️ DomainRateLimiter CDN Failover 策略
**位置：** `CommonUtilities/DomainRateLimiter.cs` & `CommonUtilities/SharedImageService.cs`

**當前問題：**
- 下載流程按順序嘗試 CDN：CloudFlare → Steam CDN → Akamai
- 如果 CloudFlare 達到並發限制（2 個），其他下載任務會等待
- 即使 Steam CDN 和 Akamai 有空閒 slots，也不會使用

**建議改進方案：**

##### 方案 A：智能 CDN 選擇器 (推薦)
```csharp
// 新增 CDN 負載均衡器
public class CdnLoadBalancer
{
    private readonly Dictionary<string, int> _domainActiveRequests;
    private readonly Dictionary<string, DateTime> _domainLastBlock;

    // 根據當前負載選擇最佳 CDN
    public string SelectBestCdn(List<string> cdnUrls)
    {
        // 1. 過濾掉最近被 block 的 CDN（5 分鐘內）
        // 2. 選擇當前並發請求數最少的 CDN
        // 3. 如果都滿了，選擇最快恢復的
    }
}
```

**優點：**
- 自動分散負載到不同 CDN
- 避免單一 CDN 過載
- 提升整體下載速度

**缺點：**
- 不同 CDN 的圖片品質可能不一致（部分遊戲）
- 需要額外的狀態管理

##### 方案 B：並行嘗試多個 CDN
```csharp
// 同時向 3 個 CDN 發起請求，使用最快回應的結果
var tasks = cdnUrls.Select(url => TryDownloadFromUrl(url, cts.Token));
var firstSuccess = await Task.WhenAny(tasks);
// 取消其他請求
cts.Cancel();
```

**優點：**
- 最快的回應速度
- 自動選擇最佳 CDN

**缺點：**
- 浪費頻寬（發起多個請求但只用一個）
- 可能更容易觸發 CDN 限制

**推薦實作：** 方案 A（智能 CDN 選擇器）

**實作步驟：**
1. 在 SharedImageService 中新增 CDN 選擇邏輯
2. 追蹤每個域名的當前並發數
3. 在 DomainRateLimiter.WaitAsync() 被阻擋時，嘗試其他 CDN
4. 記錄 429/403 回應，暫時標記該 CDN 為「已阻擋」

**程式碼位置：**
- `SharedImageService.TryDownloadLanguageSpecificImageAsync()` (line 227-335)
- 需要重構 URL 選擇邏輯

---

### 🟢 低優先級 - 程式碼品質改善

#### 4. ⏸️ SharedImageService._pendingRequests 自動清理
**位置：** `CommonUtilities/SharedImageService.cs:90-110`
**問題：** CleanupStaleRequests() 從未被自動調用
**建議：** 在每次 GetGameImageAsync() 入口處檢查並清理

#### 5. ⏸️ 語言切換批次大小優化
**位置：** `AnSAM/MainWindow.xaml.cs:845`
**當前：** `batchSize = 3, delay = 30ms`
**建議：** `batchSize = 5-8, delay = 20ms`

---

## 測試建議

### 並發數壓力測試
1. 準備包含 100+ 遊戲的測試環境
2. 清空圖片快取
3. 監控網路請求：
   - 同時下載數量
   - CDN 分布情況
   - 429/403 錯誤率
4. 測試場景：
   - 初次載入所有遊戲
   - 快速切換語言
   - 滾動瀏覽大量遊戲

### 記憶體洩漏測試
1. 使用 Visual Studio 記憶體分析工具
2. 重複執行：
   - 載入遊戲列表
   - 切換語言 10 次
   - 滾動瀏覽
3. 觀察記憶體使用趨勢

---

## 參考資料

### CDN 限制資訊
- **CloudFlare:** 通常允許較高並發，但有動態限制
- **Steam CDN:** 較嚴格，建議保守使用
- **Akamai:** 中等限制

### 相關檔案
- `CommonUtilities/SharedImageService.cs` - 圖片下載主邏輯
- `CommonUtilities/GameImageCache.cs` - 快取與並發控制
- `CommonUtilities/DomainRateLimiter.cs` - 域名限速器
- `AnSAM/MainWindow.xaml.cs` - UI 與 GameItem 類別

---

## 版本記錄
- **2026-01-01:** 初次分析，識別記憶體洩漏和並發問題
- **2026-01-01:** 修正 BitmapImage 記憶體洩漏

---

## 下次工作重點
1. 實作 CDN 負載均衡器（方案 A）
2. 進行並發數壓力測試
3. 根據測試結果調整 maxConcurrency 參數
