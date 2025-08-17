using System.Collections.Generic;
using System.Threading.Tasks;

namespace AnSAM
{
    public static class GameListService
    {
        public static Task<IReadOnlyList<SteamAppData>> LoadGamesAsync()
        {
            return Task.FromResult<IReadOnlyList<SteamAppData>>(new List<SteamAppData>());
        }
    }
}
