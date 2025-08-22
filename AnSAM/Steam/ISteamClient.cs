namespace AnSAM.Steam
{
    public interface ISteamClient
    {
        bool Initialized { get; }
        bool IsSubscribedApp(uint appId);
        string? GetAppData(uint appId, string key);
    }
}
