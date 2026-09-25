# Development Guide

## Important Implementation Details

### When Working with Steam Integration

- **Never guess interface versions**: RunGame tries multiple versions (ISteamUserStats013/012/011, ISteamUser012/020/019/018) with fallback logic
- **Always validate Steam state**: Check Steam process is running, user is logged in, AppID matches
- **Callback pump is critical**: Without the 100ms timer, Steam callbacks never fire
- **Resource cleanup matters**: Improper cleanup can leave Steam in unstable state

### When Working with Caching

- **No image expiry**: Successfully cached images never expire; adding an expiry multiplies download volume against Steam's CDNs
- **MIME validation required**: Images must be validated before caching (prevents corruption)
- **Failure backoff prevents retry storms**: `ImageFailureTrackingService` backs off exponentially per app and language (5 min, doubling to a ~14-day cap); this is intentional
- **Language isolation**: Never mix images from different language caches
- **Non-blocking downloads**: Use fire-and-forget pattern for non-critical image loads to keep UI responsive
- **Language tracking**: Always update `_loadedLanguage` when setting cover path to enable smart cache invalidation
- **Semaphore disposal safety**: Wrap semaphore.Release() in try-catch to handle disposal during shutdown/language switch

### When Adding Tests

- **Test projects use AnyCPU**: Only main executables target x64
- **Mock Steam interfaces**: Use `ISteamClient`, `ISteamUserStats` abstractions
- **Test rate limiting carefully**: Use in-memory time providers, not real delays

## Output Directory Structure

Build output goes to `output/{Configuration}/{Platform}/{TargetFramework}/{ProjectName}/`:

- Debug and Release builds are both self-contained `win-x64`; `publish.ps1` produces the Native AOT binaries

## Language Resource Filtering

`SatelliteResourceLanguages` limits which culture folders (`ja/`, `zh-Hans/`, ...) NuGet packages copy into build and publish output. Only `RunGame.csproj` sets it (`en-US;en-GB;zh-TW;ja-JP;ko-KR`). AnSAM's and MyOwnGames' current dependencies ship no satellite resource assemblies, so their output has no culture folders either.

If a new package starts adding culture folders to a project's output, set `SatelliteResourceLanguages` in that project's .csproj. Don't clean the output with a script afterwards.

## Common Development Scenarios

### Adding a new Steam interface
Steam interfaces are C++ vtables reached through `steamclient64.dll`, not COM. `AnSAM/Steam/SteamClient.cs` and `RunGame/Steam/SteamGameClient.cs` each fetch the interface pointer with a version string (e.g. `STEAMAPPS_INTERFACE_VERSION008`) and bind vtable slots to `[UnmanagedFunctionPointer(CallingConvention.ThisCall)]` delegates via `Marshal.GetDelegateForFunctionPointer`. Try several interface versions with fallback, as the existing ISteamUserStats/ISteamUser code does. RunGame falls back to `ModernSteamClient` (flat `steam_api64` API) when the legacy client fails to initialize; both implement `ISteamUserStats`.

### Adding a new image CDN source
Downloads go through `SharedImageService.TryDownloadWithCdnFailover()` (CDN choice in `CdnLoadBalancer`, display names in `CdnStatsFormatter`) into `GameImageCache.GetImagePathAsync()`. Validate with `ImageValidation` before caching, and test with rate limiting enabled.

### Modifying achievement/stat logic
1. Changes go in `RunGame/Services/GameStatsService.cs`
2. Unit tests mock `ISteamUserStats`, but VDF schema variations are complex: also check parsing against real `UserGameStatsSchema_{gameId}.bin` files
3. Watch for cascading behavior (one achievement triggering others)
4. Debug builds don't write to Steam (intentional safety)

### Updating Steam Web API integration (MyOwnGames)
1. Modify `SteamApiService.cs`
2. Adjust rate limiting in `MyOwnGames/appsettings.json` if needed
3. Steam Web API requires valid API key (not included in repo)

## Debugging Tips

- **Logging**: `AppLogger` (Serilog, `CommonUtilities/AppLogger.cs`) is the logger; `DebugLogger` is legacy
- **Steam callback debugging**: Add breakpoint in callback pump timer to see all Steam events
- **Image download debugging**: Check `ImageFailureTrackingService` to see why downloads are being skipped
- **VDF schema issues**: Inspect `UserGameStatsSchema_{gameId}.bin` with hex editor if achievement parsing fails
