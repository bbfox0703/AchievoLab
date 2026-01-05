# AnSAM vs MyOwnGames - 圖片載入機制比較分析

## 1. UI 事件觸發機制

### AnSAM
**事件：**
- ✅ `ContainerContentChanging` (Phase 0/1)
- ❌ 無 `ViewChanged` (scroll event)

**觸發條件：**
```csharp
// 只在 no_icon 時載入
if (game.IconUri == "ms-appx:///Assets/no_icon.png")
{
    await game.LoadCoverAsync(_imageService);
}
```

**優點：**
- 簡單、清晰
- 完全依賴容器重用機制
- 無額外邏輯複雜度

**缺點：**
- 只在容器重建時觸發
- 滾動時不主動載入

---

### MyOwnGames
**事件：**
- ✅ `ContainerContentChanging` (Phase 0/1)
- ✅ `ViewChanged` (scroll event)

**觸發條件：**
```csharp
// ContainerContentChanging: 檢查複雜狀態
if (_imagesSuccessfullyLoaded.Contains(key))
{
    // 檢查 UI 是否真的顯示
    if (entry.IconUri contains no_icon or ms-appx) {
        reload();
    }
}

// ViewChanged: 檢查錯誤語言、錯誤 AppID
if (IconUri contains wrong language or wrong AppID) {
    reload();
}
```

**優點：**
- 雙重保險
- 主動偵測並修正錯誤狀態

**缺點：**
- 複雜度高
- 維護成本高
- 可能與容器重用衝突

---

## 2. 圖片載入邏輯

### AnSAM - `GameItem.LoadCoverAsync()`
**位置：** GameItem 類內部（自管理）

**流程：**
```csharp
1. 檢查 _coverLoading 避免重複載入
2. 檢查 IconUri != no_icon 則跳過
3. 設置 _coverLoading = true
4. 如果非英文：
   - 先載入 English fallback（如果有快取）
   - 立即更新 UI
5. 載入目標語言圖片
6. 更新 UI
7. 設置 _coverLoading = false
```

**特點：**
- ✅ English fallback 自動處理
- ✅ 語言驗證（避免語言切換時更新錯誤）
- ✅ 狀態自包含（`_coverLoading`, `_loadedLanguage`）
- ✅ 簡單的去重機制

---

### MyOwnGames - `LoadGameImageAsync()`
**位置：** MainWindow 類內部（集中管理）

**流程：**
```csharp
1. 檢查 _imagesCurrentlyLoading 避免重複
2. 檢查 _imagesSuccessfullyLoaded（但要驗證 UI 狀態）
3. 添加到 _imagesCurrentlyLoading
4. 檢查快取
5. 呼叫 _imageService.GetGameImageAsync()
6. 更新 UI
7. 添加到 _imagesSuccessfullyLoaded
8. 移除 _imagesCurrentlyLoading
```

**特點：**
- ❌ English fallback 需手動處理（LoadOnDemandImages）
- ❌ 複雜的狀態追蹤（兩個集合）
- ❌ UI 狀態與載入狀態分離
- ✅ 更細緻的控制

---

## 3. CDN 隊列管理

### AnSAM
**機制：** 由 `SharedImageService` 統一管理
- 全域 semaphore：最多 10 個併發下載
- Per-cache semaphore：最多 4 個併發
- Domain rate limiter（token bucket）

**語言切換策略：**
```csharp
// 1. 立即載入快取的目標語言圖片（blocking）
await Task.WhenAll(cachedInTargetLanguage.Select(g => g.LoadCoverAsync()));

// 2. 背景載入 English fallback + 非快取項目（non-blocking）
backgroundLoadTasks.Add(game.LoadCoverAsync());
_ = Task.WhenAll(backgroundLoadTasks); // 不等待
```

**優點：**
- 快速回應（只等快取項目）
- 背景下載不阻塞 UI

---

### MyOwnGames
**機制：** SharedImageService + 額外的 pending queue
- MAX_PENDING_QUEUE_SIZE = 200
- Queue full → English fallback
- 每 50 次清理一次

**語言切換策略（BEFORE CLEAN SLATE）：**
```csharp
// 複雜的可見/隱藏分類
var (visibleItems, hiddenItems) = GetVisibleAndHiddenGameItems();

// 1. 載入 English fallback（visible）
await LoadEnglishFallbackImagesFirst(visibleItems);

// 2. 載入 Japanese（visible）
await LoadVisibleItemsImages(visibleItems);

// 3. LoadOnDemandImages 內部分類
categorize into: cachedInTargetLanguage, cachedInEnglishOnly, notCached
```

**語言切換策略（AFTER CLEAN SLATE）：**
```csharp
// 1. Scroll to top
// 2. Unbind GridView
// 3. Reset all IconUri = no_icon
// 4. Rebind GridView
// 5. ContainerContentChanging 按需載入
```

**優點：**
- 更精細的控制
- 避免隊列溢出

**缺點：**
- 複雜度高
- 重複邏輯（多處處理 English fallback）

---

## 4. 語言切換處理

### AnSAM
```csharp
1. Scroll to top
2. Unbind GridView (ItemsSource = null)
3. Reset all: IconUri = no_icon, ClearLoadingState()
4. Rebind GridView (ItemsSource = Games)
5. [DELETED] 複雜的可見/隱藏邏輯（已移除）
6. ContainerContentChanging 按需載入
```

**特點：**
- ✅ CLEAN SLATE approach
- ✅ 簡單可靠
- ✅ 無需追蹤可見項目

---

### MyOwnGames（現在）
```csharp
1. Scroll to top
2. Unbind GridView
3. Reset all: IconUri = no_icon
4. Clear _imagesSuccessfullyLoaded
5. Clear _imagesCurrentlyLoading
6. Rebind GridView
7. ContainerContentChanging 按需載入
```

**特點：**
- ✅ 與 AnSAM 統一
- ✅ CLEAN SLATE approach

---

## 5. 共用部分

### 已共用（CommonUtilities）
1. ✅ `SharedImageService` - 圖片下載服務
2. ✅ `GameImageCache` - 磁碟快取管理
3. ✅ `ImageFailureTrackingService` - 失敗追蹤
4. ✅ `HttpClientProvider` - HTTP 客戶端
5. ✅ `DomainRateLimiter` - 域名限流

### 可共用但未共用
1. ❌ **圖片載入狀態管理**
   - AnSAM: `_coverLoading`, `_loadedLanguage` (per GameItem)
   - MyOwnGames: `_imagesSuccessfullyLoaded`, `_imagesCurrentlyLoading` (global)

2. ❌ **English Fallback 策略**
   - AnSAM: 內建於 `LoadCoverAsync()`
   - MyOwnGames: `LoadEnglishFallbackImagesFirst()` + `LoadOnDemandImages()`

3. ❌ **CLEAN SLATE 語言切換**
   - 兩者現在邏輯相同但重複實現

4. ❌ **可見項目計算**
   - `GetVisibleAndHiddenGames()` vs `GetCurrentlyVisibleItems()`
   - 邏輯幾乎相同但分別實現

---

## 6. 核心差異分析

| 特性 | AnSAM | MyOwnGames | 建議 |
|------|-------|------------|------|
| **架構模式** | Domain model (GameItem 自管理) | Service-oriented (MainWindow 集中) | 統一為 Domain model |
| **狀態管理** | Per-item (`_coverLoading`) | Global collections | 統一為 Per-item |
| **English Fallback** | 自動（內建於 LoadCover） | 手動（分散多處） | 統一為自動 |
| **載入觸發** | ContainerContentChanging only | + ViewChanged | 簡化為 only ContainerContentChanging |
| **複雜度** | 低 | 高 | 降低 MyOwnGames 複雜度 |

---

## 7. 統一建議

### 7.1 提取到 CommonUtilities

#### `ImageLoadableItem` 介面/基類
```csharp
public interface IImageLoadableItem
{
    int AppId { get; }
    string IconUri { get; set; }

    Task LoadCoverAsync(SharedImageService imageService, string? languageOverride = null);
    void ClearLoadingState();
    bool IsCoverFromLanguage(string language);
}
```

#### `ImageLoadingHelper` 靜態類
```csharp
public static class ImageLoadingHelper
{
    // English fallback 載入邏輯
    public static async Task<string?> LoadWithEnglishFallback(
        SharedImageService service,
        int appId,
        string targetLanguage,
        Action<string> onEnglishLoaded = null)
    {
        bool isNonEnglish = targetLanguage != "english";
        bool englishCached = isNonEnglish && service.IsImageCached(appId, "english");

        if (englishCached)
        {
            var englishPath = await service.GetGameImageAsync(appId, "english");
            onEnglishLoaded?.Invoke(englishPath);
        }

        return await service.GetGameImageAsync(appId, targetLanguage);
    }
}
```

#### `CleanSlateLanguageSwitcher` 靜態類
```csharp
public static class CleanSlateLanguageSwitcher
{
    public static async Task SwitchLanguageAsync<T>(
        GridView gridView,
        ObservableCollection<T> items,
        string newLanguage,
        DispatcherQueue dispatcher) where T : IImageLoadableItem
    {
        // 1. Scroll to top
        // 2. Unbind
        // 3. Reset all items
        // 4. Rebind
        // (統一實現)
    }
}
```

---

### 7.2 GameItem/GameEntry 統一

**建議：**
1. 讓 `GameEntry` 實現 `IImageLoadableItem`
2. 移動 `LoadCoverAsync` 邏輯到 `GameEntry` 內部
3. 移除 `LoadGameImageAsync` 從 MainWindow
4. 移除 `_imagesSuccessfullyLoaded`, `_imagesCurrentlyLoading`（改用 per-item 狀態）

---

### 7.3 移除 ViewChanged 處理

**理由：**
- ContainerContentChanging 已足夠（在 CLEAN SLATE 下）
- ViewChanged 增加複雜度
- 可能與容器重用衝突

**建議：**
- 移除 `GamesGridView_ViewChanged`
- 移除 `LoadOnDemandImages`
- 依賴 ContainerContentChanging 按需載入

---

## 8. 實施計畫

### Phase 1: 提取共用邏輯到 CommonUtilities
1. 建立 `IImageLoadableItem` 介面
2. 建立 `ImageLoadingHelper` 類（English fallback）
3. 建立 `CleanSlateLanguageSwitcher` 類

### Phase 2: 統一 MyOwnGames
1. `GameEntry` 實現 `IImageLoadableItem`
2. 移植 `LoadCoverAsync` 從 AnSAM 到 `GameEntry`
3. 移除 MainWindow 中的載入邏輯
4. 移除 ViewChanged 處理

### Phase 3: AnSAM 使用共用邏輯
1. `GameItem` 使用 `ImageLoadingHelper`
2. 使用 `CleanSlateLanguageSwitcher`

### Phase 4: 清理與測試
1. 移除重複代碼
2. 全面測試兩個應用

---

## 9. 優缺點評估

### 當前狀態
| | AnSAM | MyOwnGames |
|---|-------|------------|
| **複雜度** | ⭐⭐ 低 | ⭐⭐⭐⭐⭐ 非常高 |
| **可維護性** | ⭐⭐⭐⭐ 高 | ⭐⭐ 低 |
| **效能** | ⭐⭐⭐⭐ 優 | ⭐⭐⭐ 良 |
| **可靠性** | ⭐⭐⭐⭐ 高 | ⭐⭐⭐ 中 |

### 統一後
| 特性 | 評分 |
|------|------|
| **複雜度** | ⭐⭐ 低（兩者一致）|
| **可維護性** | ⭐⭐⭐⭐⭐ 極高（共用邏輯）|
| **效能** | ⭐⭐⭐⭐ 優（無重複下載）|
| **可靠性** | ⭐⭐⭐⭐⭐ 極高（單一實現）|
| **代碼重用** | ⭐⭐⭐⭐⭐ 極高 |

---

## 10. 結論

**建議統一方向：** 採用 **AnSAM 的簡化模式**

**原因：**
1. ✅ 更簡單、更可靠
2. ✅ 已驗證可行（AnSAM 正常運作）
3. ✅ 更易維護
4. ✅ 避免過度工程

**MyOwnGames 需要移除的複雜邏輯：**
- ❌ ViewChanged 事件處理
- ❌ LoadOnDemandImages 複雜分類
- ❌ _imagesSuccessfullyLoaded 全域追蹤
- ❌ _imagesCurrentlyLoading 全域追蹤
- ❌ 手動 English fallback 處理

**MyOwnGames 需要採用的 AnSAM 邏輯：**
- ✅ GameEntry.LoadCoverAsync()（自管理）
- ✅ Per-item 狀態（_coverLoading, _loadedLanguage）
- ✅ 自動 English fallback（內建於 LoadCover）
- ✅ 只依賴 ContainerContentChanging

**共用到 CommonUtilities：**
- ✅ IImageLoadableItem 介面
- ✅ ImageLoadingHelper（English fallback）
- ✅ CleanSlateLanguageSwitcher（語言切換）
- ✅ 可見項目計算邏輯
