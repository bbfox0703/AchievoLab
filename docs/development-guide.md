# Development Guide

## Important Implementation Details

### When Working with Steam Integration

- **Never guess interface versions**: RunGame tries multiple versions (ISteamUserStats013/012/011, ISteamUser012/020/019/018) with fallback logic
- **Always validate Steam state**: Check Steam process is running, user is logged in, AppID matches
- **Callback pump is critical**: Without the 100ms timer, Steam callbacks never fire
- **Resource cleanup matters**: Improper cleanup can leave Steam in unstable state

### When Working with Caching

- **Respect TTLs**: Don't arbitrarily change cache expiration without understanding download volume impact
- **MIME validation required**: Images must be validated before caching (prevents corruption)
- **Failure tracking prevents retry storms**: 7-day failure tracking per language is intentional
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

- Debug builds: self-contained (includes all runtimes)
- Release builds: framework-dependent (requires .NET 8 + Windows App SDK 1.7 installed)

## Language Resource Filtering

Projects use `SatelliteResourceLanguages` to reduce binary size by including only supported languages. If adding a new language, update this property in all three .csproj files.

## Clean XAML Locales

Use `.\clean-languages.bat` or `.\clean-xaml-locales.ps1` to remove unwanted WinUI 3 language resource files from build output.

## Common Development Scenarios

### Adding a new Steam interface
1. Define interface in CommonUtilities with `[ComImport, Guid(...)]`
2. Add vtable method delegates with `UnmanagedFunctionPointer`
3. Update `SteamClient` or `SteamGameClient` to retrieve interface
4. Add version fallback logic (try multiple versions)

### Adding a new image CDN source
1. Update `GameImageCache.DownloadAndCacheImageAsync()`
2. Add new CDN URL to the fallback chain
3. Ensure MIME validation happens before caching
4. Test with rate limiting enabled

### Modifying achievement/stat logic
1. Changes go in `RunGame/Services/GameStatsService.cs`
2. Always test with actual Steam games, not mocks (VDF schema variations are complex)
3. Watch for cascading behavior (one achievement triggering others)
4. Debug builds don't write to Steam (intentional safety)

### Updating Steam Web API integration (MyOwnGames)
1. Modify `SteamApiService.cs`
2. Adjust rate limiting in `MyOwnGames/appsettings.json` if needed
3. Steam Web API requires valid API key (not included in repo)

## Debugging Tips

- **Enable DebugLogger output**: Check CommonUtilities/DebugLogger.cs for verbose logging
- **Steam callback debugging**: Add breakpoint in callback pump timer to see all Steam events
- **Image download debugging**: Check `ImageFailureTrackingService` to see why downloads are being skipped
- **VDF schema issues**: Inspect `UserGameStatsSchema_{gameId}.bin` with hex editor if achievement parsing fails
