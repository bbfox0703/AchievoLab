# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> 詳細規範請參考 [docs/](docs/) 目錄下的文件：
> - [docs/architecture.md](docs/architecture.md) — 系統架構、Steamworks 整合、Cache 設計、語言支援
> - [docs/development-guide.md](docs/development-guide.md) — 實作規範、開發場景、Debugging Tips

## Project Overview

AchievoLab is a 64-bit Steam achievement management GUI built with WinUI 3 and .NET 8. The solution contains three independent executables:

1. **AnSAM** - Main game library interface that discovers Steam games and launches the achievement manager
2. **RunGame** - Per-game achievement/stats editor (launched by AnSAM with AppID as parameter)
3. **MyOwnGames** - Steam Web API-based personal game collection manager (can be used for AnSAM)

All three share the **CommonUtilities** project for common services (image caching, HTTP client, logging).

## Build Commands

### Building the Solution
```powershell
# Build entire solution (Debug configuration)
dotnet build AnSAM.sln -c Debug

# Build entire solution (Release configuration)
dotnet build AnSAM.sln -c Release

# Clean build artifacts
dotnet clean AnSAM.sln
```

### Building Individual Projects
```powershell
dotnet build AnSAM/AnSAM.csproj -c Debug
dotnet build RunGame/RunGame.csproj -c Debug
dotnet build MyOwnGames/MyOwnGames.csproj -c Debug
```

### Running Tests
```powershell
# Run all tests
dotnet test AnSAM.sln

# Run specific test project
dotnet test AnSAM.Tests/AnSAM.Tests.csproj
dotnet test CommonUtilities.Tests/CommonUtilities.Tests.csproj
dotnet test MyOwnGames.Tests/MyOwnGames.Tests.csproj
```

### Running the Applications
```powershell
.\output\Debug\x64\net8.0-windows10.0.22621.0\AnSAM\AnSAM.exe
.\output\Debug\x64\net8.0-windows10.0.22621.0\RunGame\RunGame.exe <AppID>
.\output\Debug\x64\net8.0-windows10.0.22621.0\MyOwnGames\MyOwnGames.exe
```

### Publishing
```powershell
dotnet publish AnSAM/AnSAM.csproj -c Release -r win-x64
```

## Platform Requirements

- **Target Framework**: net8.0-windows10.0.22621.0
- **Minimum OS**: Windows 10 version 1904 (10.0.17763.0)
- **Architecture**: x64 only (Steam client is 64-bit)
- **Dependencies**: Windows App SDK 1.8, .NET 8
