# 語系圖片快取修正總結

## ✅ 已完成的修改

### 修改日期
2026-01-01

### 修改內容

#### 1. 移除圖片複製機制

**檔案：** `CommonUtilities/GameImageCache.cs`

**修改：**
- ✅ 修改 `TryEnglishFallbackAsync` 方法（3 處）
- ✅ 移除 `CopyToOriginalLanguageFolder` 方法

**變更詳情：**

| 位置 | 修改前 | 修改後 |
|------|--------|--------|
| Line 300-307 | 複製英文圖片到語系資料夾 | 直接返回英文圖片路徑 |
| Line 323-328 | 下載後複製到語系資料夾 | 直接返回下載的英文路徑 |
| Line 351-357 | Logo fallback 也複製 | 直接返回 Logo 路徑 |
| Line 368-384 | CopyToOriginalLanguageFolder 方法 | **已刪除** |

#### 2. 新增清理功能

**檔案：** `CommonUtilities/GameImageCache.cs`, `CommonUtilities/SharedImageService.cs`

**新增方法：**
```csharp
public int CleanupDuplicatedEnglishImages(bool dryRun = false)
```

**功能：**
- 掃描所有語系資料夾
- 比對與英文資料夾中的相同檔案
- 刪除重複的複製檔案
- 統計並報告釋放的空間

---

## 🎯 修正的問題

### 問題 1: 重試機制失效 ✅

**修正前：**
```
Day 15: 檢查失敗記錄（> 7 天）→ 應該重試
        檢查 tchinese/ 快取 → ❌ 找到複製的英文圖片
        直接使用 → ❌ 永遠不會重試
```

**修正後：**
```
Day 15: 檢查失敗記錄（> 7 天）→ 應該重試
        檢查 tchinese/ 快取 → ✅ 無複製檔案
        執行下載重試 → ✅ 正常運作
```

### 問題 2: 磁碟空間浪費 ✅

**預期節省空間：**
- 800 個遊戲 × 100KB × 4 語系 = **約 320MB**

### 問題 3: 快取一致性 ✅

**修正前：**
- 語系資料夾中的複製檔案可能是舊版本

**修正後：**
- 永遠使用最新的英文圖片作為 fallback

### 問題 4: 不必要的磁碟 I/O ✅

**效能提升：**
- 減少 **80%** 磁碟寫入操作
- 從 5 次寫入（1 英文 + 4 複製）降到 1 次寫入

---

## 🚀 使用清理功能

### 方法 1: 在 AnSAM 中執行清理

**位置：** 在 `MainWindow.xaml.cs` 中加入清理呼叫

**範例：** 在啟動時自動清理

```csharp
// MainWindow.xaml.cs
private async Task RefreshAsync()
{
    // ... 現有程式碼 ...

    // 在背景執行清理（首次啟動時）
    _ = Task.Run(() =>
    {
        try
        {
            var duplicates = _imageService.CleanupDuplicatedEnglishImages(dryRun: false);
            if (duplicates > 0)
            {
                DebugLogger.LogDebug($"Startup cleanup: removed {duplicates} duplicated images");
            }
        }
        catch (Exception ex)
        {
            DebugLogger.LogDebug($"Cleanup error: {ex.Message}");
        }
    });

    // ... 現有程式碼 ...
}
```

### 方法 2: 手動測試清理

**使用 dry run 模式先檢查：**

```csharp
// 只檢查，不刪除
var count = _imageService.CleanupDuplicatedEnglishImages(dryRun: true);
Console.WriteLine($"Found {count} duplicated files");

// 確認後執行清理
var deleted = _imageService.CleanupDuplicatedEnglishImages(dryRun: false);
Console.WriteLine($"Deleted {deleted} duplicated files");
```

### 方法 3: 加入 UI 按鈕（可選）

**在設定選單中加入「清理快取」功能：**

```csharp
private async void CleanupCache_Click(object sender, RoutedEventArgs e)
{
    StatusText.Text = "Cleaning up cache...";
    StatusProgress.IsIndeterminate = true;

    var deleted = await Task.Run(() =>
        _imageService.CleanupDuplicatedEnglishImages(dryRun: false));

    StatusProgress.IsIndeterminate = false;

    var dialog = new ContentDialog
    {
        Title = "Cache Cleanup Complete",
        Content = $"Removed {deleted} duplicated files",
        CloseButtonText = "OK",
        XamlRoot = Content.XamlRoot
    };

    await dialog.ShowAsync();
    StatusText.Text = "Ready";
}
```

---

## 📊 驗證步驟

### 測試 1: 驗證重試機制

1. **清空快取**
   ```csharp
   _imageService.ClearCache();
   ```

2. **切換到繁中模式**
   - 觀察：應該只在 `english/` 下載圖片
   - 檢查：`tchinese/` 資料夾應該是空的（或只有真正的繁中圖片）

3. **等待 8 天後重新載入**（可手動修改失敗記錄測試）
   - 應該重新嘗試下載繁中圖片
   - 不會因為找到複製檔案而跳過

### 測試 2: 驗證清理功能

1. **執行 dry run**
   ```csharp
   var count = _imageService.CleanupDuplicatedEnglishImages(dryRun: true);
   ```

2. **檢查 DebugLogger 輸出**
   ```
   [DEBUG] Checking 150 files in tchinese folder for duplicates
   [DEBUG] [DRY RUN] Would delete duplicated English image: tchinese/123.jpg (102400 bytes)
   [DEBUG] [DRY RUN] Found 120 duplicated files (12.50 MB that could be reclaimed)
   ```

3. **執行實際清理**
   ```csharp
   var deleted = _imageService.CleanupDuplicatedEnglishImages(dryRun: false);
   ```

4. **驗證結果**
   - 檢查語系資料夾中的檔案數量減少
   - 確認磁碟空間已釋放
   - 確認 UI 仍能正常顯示圖片

### 測試 3: 驗證語言切換

1. **英文 → 繁中**
   - 應顯示繁中圖片（如果存在）
   - 或顯示英文圖片作為 fallback

2. **繁中 → 日文**
   - 應正常切換
   - 不應有任何錯誤

3. **切回英文**
   - 應正常顯示
   - 圖片路徑可能是 `english/123.jpg`

---

## 📝 技術細節

### 清理邏輯

```csharp
foreach (var language in ["tchinese", "schinese", "japanese", "korean"])
{
    foreach (var file in Directory.GetFiles(languageDir))
    {
        var englishFile = Path.Combine(englishDir, fileName);

        if (File.Exists(englishFile))
        {
            // 比較檔案大小
            if (languageSize == englishSize)
            {
                // 比較檔案內容
                if (languageBytes.SequenceEqual(englishBytes))
                {
                    // 確認為重複檔案，刪除
                    File.Delete(languageFile);
                }
            }
        }
    }
}
```

### 效能考量

- **檔案大小比較：** 快速過濾，避免不必要的內容比較
- **內容比較：** 使用 `SequenceEqual` 確保完全相同
- **錯誤處理：** 個別檔案錯誤不影響整體清理
- **統計報告：** 提供詳細的清理結果

---

## ⚠️ 注意事項

### 1. 備份建議

雖然這個修改是**安全的**，但建議在首次執行清理前：
- 備份 `%LOCALAPPDATA%/AchievoLab/ImageCache/` 資料夾
- 或先使用 `dryRun: true` 測試

### 2. 語系特定圖片不會被刪除

清理功能**只刪除與英文圖片完全相同的檔案**。
如果某個遊戲真的有繁中專屬封面，這個檔案**不會被刪除**。

### 3. 清理時機

建議在以下時機執行清理：
- ✅ 首次啟動時（背景執行）
- ✅ 使用者手動觸發
- ❌ 不建議在每次啟動時都執行（影響啟動速度）

### 4. 多語系真實圖片

如果遊戲確實有不同語系的封面圖片：
- 第一次下載會失敗 → 顯示英文 fallback
- 7 天後會重試
- 如果成功下載到真正的繁中圖片 → 該圖片會被保留

---

## 🔍 Debug 輸出範例

### 正常運作

```
[2026-01-01 10:00:00] DEBUG: Attempting English fallback for 123 (original: tchinese)
[2026-01-01 10:00:00] DEBUG: Found existing English cached image for 123, using directly as fallback
[2026-01-01 10:00:00] DEBUG: Removed failed download record for 123 (tchinese) - download now successful
```

### 清理執行

```
[2026-01-01 10:05:00] DEBUG: Checking 150 files in tchinese folder for duplicates
[2026-01-01 10:05:01] DEBUG: Deleted duplicated English image: tchinese/123.jpg (102400 bytes)
[2026-01-01 10:05:01] DEBUG: Deleted duplicated English image: tchinese/456.jpg (98304 bytes)
...
[2026-01-01 10:05:05] DEBUG: Cleaned up 120 duplicated files, reclaimed 12.50 MB of disk space
```

---

## 📦 檔案清單

### 修改的檔案
- `CommonUtilities/GameImageCache.cs`
- `CommonUtilities/SharedImageService.cs`

### 新增的文件
- `docs/LANGUAGE_IMAGE_CACHE_ANALYSIS.md` - 詳細分析
- `docs/LANGUAGE_CACHE_FIX_SUMMARY.md` - 此文件

---

## ✅ 檢查清單

執行以下檢查以確認修改成功：

- [x] CommonUtilities 編譯成功（0 錯誤，0 警告）
- [x] AnSAM 編譯成功
- [x] RunGame 編譯成功
- [x] MyOwnGames 編譯成功
- [x] 移除了 CopyToOriginalLanguageFolder 方法
- [x] TryEnglishFallbackAsync 不再複製檔案
- [x] 新增了 CleanupDuplicatedEnglishImages 方法
- [x] SharedImageService 公開了清理 API

---

## 🎉 預期效果

### 立即效果
- ✅ 新下載的圖片不會再被複製
- ✅ 減少磁碟寫入操作
- ✅ 確保快取一致性

### 執行清理後
- ✅ 釋放 100-500 MB 磁碟空間（取決於遊戲數量）
- ✅ 移除重複的舊版本圖片
- ✅ 重試機制正常運作

### 長期效果
- ✅ 維護更簡單（只需管理英文快取）
- ✅ 效能更好（更少的檔案 I/O）
- ✅ 使用者體驗更好（永遠顯示最新圖片）

---

## 📞 問題回報

如果發現任何問題，請檢查：

1. **DebugLogger 輸出**
   - 是否有錯誤訊息
   - 清理是否正常執行

2. **磁碟空間**
   - 檢查語系資料夾大小變化

3. **圖片顯示**
   - 切換語言是否正常
   - 圖片是否正確顯示

4. **快取目錄**
   - `%LOCALAPPDATA%/AchievoLab/ImageCache/english/` - 應該有圖片
   - `%LOCALAPPDATA%/AchievoLab/ImageCache/tchinese/` - 應該只有真正的繁中圖片（或為空）

---

**修改完成！可以開始使用。** 🚀
