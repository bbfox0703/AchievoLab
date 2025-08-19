# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SteamAchievementManagerX2 is a modern WinUI 3 application that provides a better-looking UI for Steam Achievement Manager (SAM). The project consists of two main parts:

1. **AnSAM** - Modern WinUI 3 application targeting .NET 8 (Windows 10 17763+)
2. **Legacy** - Original SAM components that AnSAM wraps and calls

## Build and Development Commands

### Building the Application
```bash
# Restore dependencies
dotnet restore AnSAM.sln

# Build the WinUI application (requires Windows with targeting enabled)
dotnet build AnSAM.sln -p:EnableWindowsTargeting=true

# Run the AnSAM application
dotnet run --project AnSAM/AnSAM.csproj -p:EnableWindowsTargeting=true

# Run the AnSAM_RunGame application (requires game ID parameter)
dotnet run --project AnSAM_RunGame/AnSAM_RunGame.csproj -p:EnableWindowsTargeting=true -- <GameID>
```

### Testing
```bash
# Run unit tests
dotnet test AnSAM.Tests/AnSAM.Tests.csproj
```

### Publishing
The project supports multiple Windows architectures:
- x86: `AnSAM/Properties/PublishProfiles/win-x86.pubxml`
- x64: `AnSAM/Properties/PublishProfiles/win-x64.pubxml`
- ARM64: `AnSAM/Properties/PublishProfiles/win-arm64.pubxml`

### Output Directory
Both Debug and Release builds output to:
- Debug: `./output/Debug/$(Platform)/`
- Release: `./output/Release/$(Platform)/`

## Architecture

### Core Components

**AnSAM (Main Application)**
- `MainWindow.xaml/.cs` - Primary UI window with game list and search functionality
- `App.xaml/.cs` - Application entry point and Steam client initialization
- `SteamAppData.cs` - Data model for Steam application information

**Services Layer**
- `GameCacheService.cs` - Handles global game list download, ownership reconciliation, and user cache updates
- `GameListService.cs` - Manages filtering and searching of game collections
- `IconCache.cs` - Downloads, caches, and manages game cover icons with size optimization
- `GameLauncher.cs` - Handles launching games via Steam URI, executable paths, or Steam links

**Steam Integration**
- `SteamClient.cs` - Direct steamclient64.dll wrapper for Steamworks API calls
- `ISteamClient.cs` - Interface for Steam operations (ownership queries, metadata)
- `GameImageUrlResolver.cs` - Resolves Steam store image URLs for game covers

**Legacy Integration**
The application calls the original SAM.Game executable located in the `.\SAM` folder to perform actual achievement management. The Legacy folder contains the original SAM source code but is not part of the build process.

### Key Architectural Patterns

1. **Service-based architecture** - Core functionality separated into focused service classes
2. **Async/await pattern** - All Steam API calls and file operations are asynchronous
3. **Caching strategy** - Multi-layer caching for game lists, icons, and metadata
4. **Direct API integration** - Uses steamclient64.dll directly rather than Steam SDK

### Platform Requirements

- Windows 10 17763 or later (due to WinUI 3 requirements)
- .NET 8 Windows runtime
- Steam client must be running for Steamworks API calls
- Windows 64-bit only (due to steamclient64.dll dependency)

### Testing Infrastructure

- xUnit framework for unit tests
- Tests focus on core services: `GameCacheService`, `IconCache`
- Test project links to source files rather than referencing assemblies