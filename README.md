# SteamAchievementManagerX2

A better looking Steam Achievement Manager GUI, built with WinUI 3 and .NET 8.

## Features
- Wraps the Steamworks API directly via `steamclient64.dll` to query ownership and metadata for installed apps.
- Launch dedicated game achievement interface
- Downloads and caches the global game list.
- Fetches, caches and reuses cover icons for games.
- Launches games through custom URI schemes, executables or Steam links.
- Also can launch game from this UI by right click menu.
- It's Windows 64 bit only.
- Can call original SAM.Game if it's placed under .\SAM folder.


## Getting Started
1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download).
2. Clone the repository and restore dependencies:
   ```bash
   git clone https://github.com/bbfox0703/SteamAchievementManagerX2.git
   cd SteamAchievementManagerX2
   dotnet restore AnSAM.sln
   ```
3. Build the WinUI application (requires Windows 10 17763 or later):
   ```bash
   dotnet build AnSAM.sln -p:EnableWindowsTargeting=true
   ```
4. Run the application:
   ```bash
   dotnet run --project AnSAM.csproj -p:EnableWindowsTargeting=true
   ```

## Tests
Unit tests cover some core services of AnSAM, such as icon caching. Run them with:
```bash
dotnet test AnSAM.Tests/AnSAM.Tests.csproj
```

## License
This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
Use origin SteamAchievementManager game data source, it's Zlib license.
