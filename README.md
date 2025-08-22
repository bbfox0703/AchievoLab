<img src="./docs/res/AchievoLabP.png" alt="AchievoLab" width="250"/>  
  
# AchievoLab
A x64 Steam achievement management GUI, built with WinUI 3 and .NET 8. under Windows 10 (1904) / 11

## Features
- Achievement management part: wraps the Steamworks API directly via `steamclient64.dll` to query ownership and metadata for installed apps.
- Launch dedicated game achievement management interface.
- Downloads and caches the global game list.
- Fetches, caches and reuses cover icons for games.
- Can launches games through GUI by right click popup menu.
- Also can launch game from this UI by right click menu.
- Get your game list data / localized game title name (if any) by Steam Web API

## Program list
1. ./AnASM/AnASM.exe: primary GUI interface.
2. ./RunGame/RunGame.exe: called by AnASM.exe when double click on a game title image.
3. ./MyOwnGames/MyOwnGames.exe: This tool is designed to get your Steam account owned game data by Steam Web API. Notice: thios may take some time to complete because Steam API will block API key for certain time if query too fast.

## To use MyOwnGames
1. Need a Steam API key. (Apply API key here)[https://steamcommunity.com/dev/apikey]. Please note API key is very important to your Steam account. Don't share it to anyone.
2. Need your SteamID64 number: Several ways to get this. 
This tool save your game list data and will be used for main program AnSAM. 

## For reference: How to find your SteamID64
1. Log in to Steam: Access your Steam account through the web browser or the Steam client. 
2. Go to your Profile: Click on your username in the top-right corner. 
3. Select "Account details": From the dropdown menu, choose "Account details" or "Edit Profile". 
4. Locate your ID: Your 17-digit SteamID64 will be listed near the top of the page, below your username. 

If it's not working: this method may be out of date, find a new way yourself.

## License
This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.  
Use [SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager) cloud game data source, this part is Zlib license.
