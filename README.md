# SteamAchievementManagerX2

Another Steam Achievement Manager (AnSAM) built with WinUI 3 and .NET 8.

## Features
- Wraps the Steamworks API directly via `steamclient64.dll` to query ownership and metadata for installed apps.
- Downloads and caches the global game list used by SAM.
- Fetches, caches and reuses cover icons for games.
- Launches games through custom URI schemes, executables or Steam links.

## Getting Started
1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download).
2. Clone the repository and restore dependencies:
   ```bash
   git clone https://github.com/bbfox0703/SteamAchievementManagerX2.git
   cd SteamAchievementManagerX2
   dotnet restore AnSAM/AnSAM.sln
   ```
3. Build the WinUI application (requires Windows 10 17763 or later):
   ```bash
   dotnet build AnSAM/AnSAM.sln -p:EnableWindowsTargeting=true
   ```
4. Run the application:
   ```bash
   dotnet run --project AnSAM/AnSAM.csproj -p:EnableWindowsTargeting=true
   ```

## Tests
Unit tests cover core services such as icon caching. Run them with:
```bash
dotnet test AnSAM.Tests/AnSAM.Tests.csproj
```

## License
This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
