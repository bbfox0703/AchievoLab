using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CommonUtilities
{
    /// <summary>
    /// Provides CLEAN SLATE language switching logic for ItemsControl-based game lists.
    /// This approach prevents container recycling issues by completely resetting the UI.
    /// </summary>
    public static class CleanSlateLanguageSwitcher
    {
        /// <summary>
        /// Performs a CLEAN SLATE language switch for an ItemsControl containing IImageLoadableItem items.
        /// </summary>
        /// <typeparam name="T">The type of items in the collection (must implement IImageLoadableItem).</typeparam>
        /// <param name="itemsControl">The ItemsControl to reset.</param>
        /// <param name="items">The collection of items.</param>
        /// <param name="newLanguage">The new language to switch to.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task SwitchLanguageAsync<T>(
            ItemsControl itemsControl,
            IEnumerable<T> items,
            string newLanguage) where T : IImageLoadableItem
        {
            try
            {
                AppLogger.LogDebug($"Starting CLEAN SLATE language switch to {newLanguage}");

                if (itemsControl == null)
                {
                    AppLogger.LogDebug("ERROR: itemsControl is null");
                    throw new ArgumentNullException(nameof(itemsControl));
                }

                AppLogger.LogDebug("Parameters validated, finding ScrollViewer...");

                // STEP 1: Scroll to top (prevents crash when unbinding at bottom of list)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var scrollViewer = itemsControl.GetVisualDescendants()
                        .OfType<ScrollViewer>()
                        .FirstOrDefault();

                    if (scrollViewer != null)
                    {
                        scrollViewer.Offset = new Avalonia.Vector(0, 0);
                        AppLogger.LogDebug("Scrolled to top");
                    }
                });
                await Task.Delay(100);

                // STEP 2: Unbind ItemsControl from collection (prevents crashes during reset)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    itemsControl.ItemsSource = null;
                    AppLogger.LogDebug("Unbound ItemsControl ItemsSource");
                });
                await Task.Delay(100);

                // STEP 3: Reset all items to clean state
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var item in items)
                    {
                        item.IconUri = ImageLoadingHelper.GetNoIconPath();
                        item.ClearLoadingState();
                    }
                    AppLogger.LogDebug($"Reset {items.Count()} items to no_icon");
                });
                await Task.Delay(100);

                // STEP 4: Rebind ItemsControl (forces container recreation)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    itemsControl.ItemsSource = items;
                    AppLogger.LogDebug("Rebound ItemsControl ItemsSource - containers will recreate");
                });

                AppLogger.LogDebug($"CLEAN SLATE language switch complete.");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error during CLEAN SLATE language switch: {ex.GetType().Name}: {ex.Message}");
                AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    AppLogger.LogDebug($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                throw;
            }
        }
    }
}
