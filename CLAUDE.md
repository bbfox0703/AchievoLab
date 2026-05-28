# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> 詳細規範請參考 [docs/](docs/) 目錄下的文件：
> - [docs/architecture.md](docs/architecture.md) — 系統架構、Steamworks 整合、Cache 設計、語言支援
> - [docs/development-guide.md](docs/development-guide.md) — 實作規範、開發場景、Debugging Tips

## Project Overview

AchievoLab is a 64-bit Steam achievement management GUI built with Avalonia 12 (UI) on .NET 10, published as Native AOT (`win-x64`, self-contained). The solution contains three independent executables:

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
.\output\Debug\x64\net10.0\AnSAM\AnSAM.exe
.\output\Debug\x64\net10.0\RunGame\RunGame.exe <AppID>
.\output\Debug\x64\net10.0\MyOwnGames\MyOwnGames.exe
```

### Publishing (Native AOT)
```powershell
# All three executables (Release, win-x64, self-contained, AOT) + PDB cleanup
.\publish.ps1

# Single project
dotnet publish AnSAM/AnSAM.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o publish/AnSAM
```

## Platform Requirements

- **Target Framework**: net10.0
- **UI / Runtime**: Avalonia 12.0.3, .NET 10, Native AOT (`PublishAot=true`)
- **Minimum OS**: Windows 10 version 2004 (10.0.19041.0)
- **Architecture**: x64 only (Steam client is 64-bit)
