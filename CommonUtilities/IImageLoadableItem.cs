using System.Threading.Tasks;

namespace CommonUtilities
{
    /// <summary>
    /// Interface for game items that support asynchronous image loading with language support.
    /// </summary>
    public interface IImageLoadableItem
    {
        /// <summary>
        /// Unique identifier for the game (Steam AppID).
        /// </summary>
        int AppId { get; }

        /// <summary>
        /// URI of the current icon/cover image.
        /// </summary>
        string IconUri { get; set; }

        /// <summary>
        /// Asynchronously loads the game's cover image using the shared image service.
        /// </summary>
        /// <param name="imageService">The image service to use for loading.</param>
        /// <param name="languageOverride">Optional language override. If null, uses current global language.</param>
        /// <param name="forceReload">If true, forces reload even if a valid image is already loaded (for language upgrades).</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task LoadCoverAsync(SharedImageService imageService, string? languageOverride = null, bool forceReload = false);

        /// <summary>
        /// Clears the loading state to allow reloading.
        /// </summary>
        void ClearLoadingState();

        /// <summary>
        /// Checks if the current cover image is from the specified language.
        /// </summary>
        /// <param name="language">The language to check.</param>
        /// <returns>True if the cover is from the specified language.</returns>
        bool IsCoverFromLanguage(string language);
    }
}
