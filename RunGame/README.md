# RunGame

RunGame 是使用 .NET 8 和 WinUI 3 開發的成就管理工具，提供現代化的成就管理界面。

## 功能特色

- **成就管理**：查看、解鎖/鎖定、批量操作遊戲成就
- **統計數據管理**：查看和修改遊戲統計數據（需要確認）
- **計時器功能**：設定成就自動解鎖計時器
- **自動滑鼠移動**：防止系統閒置的滑鼠微動功能
- **多語言支持**：支持 Steam 的多種語言介面
- **現代化 UI**：使用 WinUI 3 CommandBar 和現代控件
- **主題自適應**：自動跟隨 Windows 淺色/深色主題

## 使用方式

```bash
# 直接從命令行啟動（需要遊戲ID）
RunGame.exe <GameID>

# 或由主程式調用
```

## 架構設計

- **不依賴 Legacy 代碼**：完全重新實作，不調用 `.\Legacy` 下的任何函數
- **直接 Steam API 集成**：使用 steamclient64.dll 直接調用 Steamworks API
- **服務導向架構**：
  - `SteamGameClient`：Steam API 包裝器
  - `GameStatsService`：遊戲統計和成就業務邏輯
  - `ThemeService`：主題管理服務
- **MVVM 模式**：數據模型支持雙向綁定和屬性通知

## 技術實現

- **目標框架**：.NET 8 (Windows 10 17763+)
- **UI 框架**：WinUI 3
- **數據解析**：自實作 KeyValue 二進制格式解析器
- **Steam 集成**：直接 P/Invoke steamclient64.dll
- **主題支持**：讀取 Windows 註冊表自動切換主題

## 構建要求

- Windows 10 17763 或更高版本
- .NET 8 SDK
- Visual Studio 2022 或 VS Code (可選)

構建命令：
```bash
dotnet build RunGame.csproj -p:EnableWindowsTargeting=true
```