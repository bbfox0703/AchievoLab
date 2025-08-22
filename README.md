<img src="./docs/res/AchievoLabP.png" alt="AchievoLab" width="250"/>  
  
# AchievoLab
A 64-bit Steam achievement management GUI built with WinUI 3 and .NET 8, compatible with Windows 10 (1904) and Windows 11.

## Features
- Directly wraps the Steamworks API via `steamclient64.dll` to query ownership and metadata of installed apps.
- Provides a dedicated achievement management interface for each game.
- Downloads and caches the global Steam game list.
- Fetches, caches, and reuses cover icons for games.
- Allows launching games directly from the GUI via right-click context menu.
- Retrieves your owned game list and localized game titles (if available) through the Steam Web API.

## Program List
1. **./AnASM/AnASM.exe**  
   The main GUI interface.
2. **./RunGame/RunGame.exe**  
   Invoked by *AnASM.exe* when a game cover is double-clicked. Used to manage achievements for the selected game.
3. **./MyOwnGames/MyOwnGames.exe**  
   Retrieves your owned game data from the Steam Web API.  
   ⚠️ Note: This process may take some time. The Steam API may temporarily block your key if requests are made too frequently.

## Using *MyOwnGames*
1. Obtain a Steam API key from [Steam Developer](https://steamcommunity.com/dev/apikey).  
   ⚠️ Your API key is sensitive and tied to your Steam account. **Do not share it with anyone.**
2. Obtain your **SteamID64**. This tool saves your game list data, which will then be used by the main program (*AnASM*).

## How to Find Your SteamID64
1. Log in to Steam via the client or web browser.  
2. Go to your **Profile** by clicking your username in the top-right corner.  
3. Select **Account details** (or **Edit Profile**).  
4. Your 17-digit SteamID64 will be shown near the top of the page, below your username.  

> If this method no longer works, please look up an updated way to obtain your SteamID64.

## License
This project is licensed under the [MIT License](LICENSE).  
It also makes use of cloud game data from [SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager), which is licensed under Zlib.

