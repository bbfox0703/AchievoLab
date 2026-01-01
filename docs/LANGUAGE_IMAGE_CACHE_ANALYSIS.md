# 語系圖片快取機制分析報告

## 當前實作問題分析

### 現況描述

當前系統在處理不同語系的遊戲封面圖片時，採用了**強制複製**英文基底圖片到各語系資料夾的策略。

### 問題點

#### 1. ❌ 不必要的磁碟空間浪費

**位置：** `GameImageCache.cs:379-398` (CopyToOriginalLanguageFolder)

```csharp
private void CopyToOriginalLanguageFolder(string englishImagePath, string cacheKey, string originalLanguage)
{
    var originalDir = GetCacheDir(originalLanguage);
    var targetPath = Path.Combine(originalDir, cacheKey + extension);
    File.Copy(englishImagePath, targetPath, overwrite: true);  // ❌ 複製檔案
}
```

**影響：**
- 假設有 1000 個遊戲，每張圖片 100KB
- 支援 5 個語系（英文、繁中、日文、韓文、簡中）
- 如果 800 個遊戲沒有語系化封面

**磁碟空間浪費：**
```
800 遊戲 × 100KB × 4 語系（非英文） = 320MB
```

#### 2. ❌ 快取一致性問題

**場景：**
1. 第一次載入繁中，下載失敗 → 複製英文圖片到 `tchinese/`
2. 30 天後，英文圖片過期並更新
3. 繁中資料夾中的**舊英文圖片仍然存在且有效**（< 30 天）
4. 使用者看到的是舊版本英文圖片

**程式碼證據：**

`SharedImageService.cs:187-199`
```csharp
// Step 2: Check language-specific cache
var diskCachedPath = _cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback: false);
if (!string.IsNullOrEmpty(diskCachedPath))  // ❌ 會找到舊的複製檔案
{
    if (IsFreshImage(diskCachedPath))  // ❌ 仍在 30 天內，判定為有效
    {
        return diskCachedPath;  // ❌ 返回舊版本
    }
}
```

#### 3. ❌ 重試機制失效

**問題：** 一旦複製英文圖片到語系資料夾，系統將**永遠不會重試**下載該語系的圖片。

**流程分析：**

```
Day 1: 嘗試下載繁中圖片 → 失敗 → 複製英文圖片到 tchinese/
Day 8: 重新載入繁中
  ↓
  檢查失敗記錄 (7 天內) → 跳過下載 ✓ (正確)
  ↓
  檢查 tchinese/ 快取 → ❌ 找到複製的英文圖片 (< 30 天)
  ↓
  直接使用 → ❌ 不會重試下載

Day 15: 重新載入繁中
  ↓
  檢查失敗記錄 (> 7 天) → 應該重試 ✓
  ↓
  檢查 tchinese/ 快取 → ❌ 仍然找到複製的英文圖片 (< 30 天)
  ↓
  直接使用 → ❌ 跳過重試！

永遠不會再嘗試下載繁中圖片，直到 30 天後檔案過期
```

**程式碼證據：**

`SharedImageService.cs:179-184`
```csharp
// Step 1: Check failure tracking
if (_cache.ShouldSkipDownload(appId, language))  // 7 天內才會 skip
{
    return await TryEnglishFallbackAsync(appId, language, cacheKey);
}
```

但是：

`SharedImageService.cs:186-199`
```csharp
// Step 2: Check language-specific cache
var diskCachedPath = _cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback: false);
if (!string.IsNullOrEmpty(diskCachedPath))  // ❌ 7 天後仍會找到複製的檔案
{
    return diskCachedPath;  // ❌ 直接返回，不執行 Step 3 下載
}

// Step 3: Try to download language-specific image
// ❌ 永遠不會執行到這裡
```

#### 4. ❌ 顯示邏輯不需要複製

**事實：** SharedImageService 已經正確處理 fallback，不需要依賴檔案複製。

**證據：**

`SharedImageService.cs:343-365`
```csharp
private async Task<string?> TryEnglishFallbackAsync(int appId, string targetLanguage, string cacheKey)
{
    // Step 6: Check English cache first
    var englishCachedPath = _cache.TryGetCachedPath(appId.ToString(), "english", checkEnglishFallback: false);
    if (!string.IsNullOrEmpty(englishCachedPath))
    {
        _imageCache[cacheKey] = englishCachedPath;  // ✓ 直接使用英文路徑
        return englishCachedPath;  // ✓ 返回英文圖片路徑
    }
}
```

UI 層不在乎路徑是 `english/123.jpg` 還是 `tchinese/123.jpg`，都能正常顯示。

---

## 正確的實作方式

### 建議方案：移除複製機制

#### 修改 1: 移除 CopyToOriginalLanguageFolder 呼叫

**GameImageCache.cs:288-377 (TryEnglishFallbackAsync)**

**修改前：**
```csharp
private async Task<ImageResult?> TryEnglishFallbackAsync(...)
{
    var existingEnglishPath = TryGetCachedPath(cacheKey, "english", checkEnglishFallback: false);
    if (!string.IsNullOrEmpty(existingEnglishPath))
    {
        CopyToOriginalLanguageFolder(existingEnglishPath, cacheKey, originalLanguage);  // ❌ 移除
        var finalPath = Path.Combine(GetCacheDir(originalLanguage), ...);  // ❌ 移除
        return new ImageResult(finalPath, false);  // ❌ 返回複製後的路徑
    }
}
```

**修改後：**
```csharp
private async Task<ImageResult?> TryEnglishFallbackAsync(...)
{
    var existingEnglishPath = TryGetCachedPath(cacheKey, "english", checkEnglishFallback: false);
    if (!string.IsNullOrEmpty(existingEnglishPath))
    {
        // ✓ 直接返回英文圖片路徑，不複製
        return new ImageResult(existingEnglishPath, false);
    }
}
```

**優點：**
1. ✅ 節省磁碟空間
2. ✅ 英文圖片更新時，所有語系立即生效
3. ✅ 重試機制正常運作
4. ✅ 程式碼更簡潔

#### 修改 2: 清理現有的複製檔案（可選）

**新增清理方法：**

```csharp
/// <summary>
/// 清理所有語系資料夾中複製的英文圖片
/// </summary>
public void CleanupDuplicatedEnglishImages()
{
    var languages = new[] { "tchinese", "schinese", "japanese", "korean" };
    var englishDir = GetCacheDir("english");

    foreach (var language in languages)
    {
        var languageDir = GetCacheDir(language);
        if (!Directory.Exists(languageDir))
            continue;

        foreach (var file in Directory.GetFiles(languageDir))
        {
            var fileName = Path.GetFileName(file);
            var englishFile = Path.Combine(englishDir, fileName);

            // 如果英文資料夾中存在相同檔案
            if (File.Exists(englishFile))
            {
                try
                {
                    // 比較檔案內容（可選，更嚴謹）
                    var languageBytes = File.ReadAllBytes(file);
                    var englishBytes = File.ReadAllBytes(englishFile);

                    if (languageBytes.SequenceEqual(englishBytes))
                    {
                        File.Delete(file);  // 刪除重複檔案
                        DebugLogger.LogDebug($"Deleted duplicated English image: {file}");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error cleaning up {file}: {ex.Message}");
                }
            }
        }
    }
}
```

---

## 重試機制正確運作驗證

### 修改後的流程

```
Day 1: 嘗試下載繁中圖片
  ↓
  檢查失敗記錄 → 無記錄 ✓
  ↓
  檢查 tchinese/ 快取 → 無檔案 ✓
  ↓
  嘗試下載 → 失敗 ✓
  ↓
  記錄失敗（7 天）✓
  ↓
  英文 Fallback → 使用 english/123.jpg ✓

Day 8: 重新載入繁中
  ↓
  檢查失敗記錄 (< 7 天) → 跳過下載 ✓
  ↓
  檢查 tchinese/ 快取 → ✅ 無檔案（沒有複製）
  ↓
  英文 Fallback → 使用 english/123.jpg ✓

Day 15: 重新載入繁中
  ↓
  檢查失敗記錄 (> 7 天) → ✅ 應該重試
  ↓
  檢查 tchinese/ 快取 → ✅ 無檔案（沒有複製）
  ↓
  ✅ 執行 Step 3：嘗試下載繁中圖片
  ↓
  如果成功 → 快取到 tchinese/123.jpg ✓
  如果失敗 → 重新記錄失敗，使用英文 Fallback ✓
```

### 關鍵差異

| 步驟 | 修改前 | 修改後 |
|------|--------|--------|
| 7 天後重試 | ❌ 找到複製檔案，跳過 | ✅ 無複製檔案，執行重試 |
| 磁碟使用 | 800 遊戲 × 4 語系 = 3200 個複製檔案 | 0 個複製檔案 |
| 快取一致性 | ❌ 可能顯示舊版本 | ✅ 永遠顯示最新英文版本 |

---

## 語言切換顯示邏輯驗證

### 當前 AnSAM 語言切換流程

**MainWindow.xaml.cs:228-229**
```csharp
// Refresh game images for the selected language
await RefreshGameImages(lang);
```

**MainWindow.xaml.cs:838-842**
```csharp
// Clear current images so new ones will load for the selected language
foreach (var game in _allGames)
{
    game.CoverPath = null;  // 清空 UI 顯示路徑
}
```

**MainWindow.xaml.cs:848-851**
```csharp
var batch = visibleGames.Skip(i).Take(batchSize)
                         .Select(g => g.LoadCoverAsync(_imageService));
await Task.WhenAll(batch);
```

**GameItem.LoadCoverAsync (MainWindow.xaml.cs:1046-1089)**
```csharp
var path = await imageService.GetGameImageAsync(ID, language).ConfigureAwait(false);
if (!string.IsNullOrEmpty(path) && Uri.TryCreate(path, UriKind.Absolute, out var localUri))
{
    CoverPath = localUri;  // ✅ 設定新路徑（可能是 english/123.jpg 或 tchinese/123.jpg）
}
```

### 驗證結果

**✅ 當前實作已正確處理，不需修改**

**原因：**
1. UI 層只在乎**檔案路徑**，不在乎路徑中的語系資料夾名稱
2. BitmapImage 可以正常載入 `file:///...english/123.jpg` 或 `file:///...tchinese/123.jpg`
3. SharedImageService 已經在記憶體快取中正確處理語系對應

**測試案例：**

```
場景 1：英文 → 繁中 (有繁中圖片)
  1. 英文模式：顯示 english/123.jpg
  2. 切換繁中：CoverPath = null
  3. 重新載入：GetGameImageAsync → 找到 tchinese/123.jpg
  4. 顯示繁中圖片 ✅

場景 2：英文 → 繁中 (無繁中圖片)
  1. 英文模式：顯示 english/123.jpg
  2. 切換繁中：CoverPath = null
  3. 重新載入：GetGameImageAsync → 下載失敗 → 返回 english/123.jpg
  4. 顯示英文圖片（作為 fallback）✅

場景 3：繁中 → 英文
  1. 繁中模式：顯示 english/123.jpg (fallback)
  2. 切換英文：CoverPath = null
  3. 重新載入：GetGameImageAsync → 找到 english/123.jpg
  4. 顯示英文圖片 ✅
```

**結論：** 語言切換邏輯**完全正常**，不需修改。

---

## 實作建議

### 優先級 1：移除複製機制（高優先）

**影響範圍：**
- `CommonUtilities/GameImageCache.cs`
  - 修改 `TryEnglishFallbackAsync` 方法
  - 移除 `CopyToOriginalLanguageFolder` 方法

**修改內容：**
1. 不再複製英文圖片到語系資料夾
2. 直接返回英文圖片路徑

**優點：**
- 立即節省磁碟空間
- 修復重試機制
- 確保快取一致性

**風險：**
- **無風險**（UI 層已正確處理）

### 優先級 2：清理現有複製檔案（中優先）

**建議時機：**
- 在 AnSAM 啟動時執行一次
- 或提供「清理快取」功能按鈕

**優點：**
- 釋放已浪費的磁碟空間
- 讓舊資料也受益於新機制

**風險：**
- 首次執行會花費一些時間（取決於檔案數量）
- 可在背景執行，不影響使用者

---

## 測試計畫

### 單元測試

```csharp
[Fact]
public async Task TryEnglishFallback_ShouldReturnEnglishPath_NotCopy()
{
    // Arrange
    var cache = new GameImageCache(...);
    // 建立英文圖片
    CreateTestImage("english/123.jpg");

    // Act
    var result = await cache.TryEnglishFallbackAsync("123", "tchinese", 123, CancellationToken.None);

    // Assert
    Assert.NotNull(result);
    Assert.Contains("english", result.Value.Path);  // ✓ 應該是英文路徑
    Assert.False(File.Exists("tchinese/123.jpg"));  // ✓ 不應該複製
}
```

### 整合測試

1. **清空所有快取**
2. **載入繁中模式**
3. **觀察：**
   - 應該只在 `english/` 資料夾下載圖片
   - `tchinese/` 資料夾應該為空（或只有真正的繁中圖片）
4. **切換到日文模式**
5. **觀察：**
   - 應該重複使用 `english/` 的圖片
   - `japanese/` 資料夾應該為空（或只有真正的日文圖片）

---

## 效能影響評估

### 磁碟 I/O

**修改前：**
- 下載英文圖片：1 次寫入 (`english/`)
- 複製到其他語系：4 次寫入 (`tchinese/`, `schinese/`, `japanese/`, `korean/`)
- **總計：5 次寫入**

**修改後：**
- 下載英文圖片：1 次寫入 (`english/`)
- **總計：1 次寫入**

**效能提升：** 減少 80% 磁碟寫入操作

### 記憶體使用

- **無影響**（記憶體快取不依賴檔案位置）

### 網路流量

- **無影響**（不會額外下載）

---

## 結論

### 問題總結

1. ❌ 複製機制浪費磁碟空間（每個遊戲 × 4 語系）
2. ❌ 導致快取一致性問題（舊版本英文圖片）
3. ❌ 破壞重試機制（永遠不會重試語系化圖片）
4. ❌ 增加不必要的磁碟 I/O

### 解決方案

1. ✅ 移除 `CopyToOriginalLanguageFolder` 呼叫
2. ✅ 直接返回英文圖片路徑作為 fallback
3. ✅ 清理現有的複製檔案（可選）

### 實作優先級

1. **立即修改：** 移除複製機制
2. **後續優化：** 新增清理功能

### 相容性

- ✅ UI 層完全相容（已驗證）
- ✅ 語言切換邏輯正常運作
- ✅ 無需修改 AnSAM 或 MyOwnGames

### 風險評估

- **風險：無**
- **回退策略：** 如果有問題，只需還原這一次 commit

**建議：立即實作此修改** 🚀
