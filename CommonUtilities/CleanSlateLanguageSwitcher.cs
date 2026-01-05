using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CommonUtilities
{
    /// <summary>
    /// Provides CLEAN SLATE language switching logic for GridView-based game lists.
    /// This approach prevents container recycling issues by completely resetting the UI.
    /// </summary>
    public static class CleanSlateLanguageSwitcher
    {
        /// <summary>
        /// Performs a CLEAN SLATE language switch for a GridView containing IImageLoadableItem items.
        /// </summary>
        /// <typeparam name="T">The type of items in the collection (must implement IImageLoadableItem).</typeparam>
        /// <param name="gridView">The GridView control to reset.</param>
        /// <param name="items">The collection of items.</param>
        /// <param name="newLanguage">The new language to switch to.</param>
        /// <param name="dispatcher">The DispatcherQueue for UI thread operations.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task SwitchLanguageAsync<T>(
            GridView gridView,
            IEnumerable<T> items,
            string newLanguage,
            DispatcherQueue dispatcher) where T : IImageLoadableItem
        {
            try
            {
                DebugLogger.LogDebug($"Starting CLEAN SLATE language switch to {newLanguage}");

                // Validate parameters
                if (gridView == null)
                {
                    DebugLogger.LogDebug("ERROR: gridView is null");
                    throw new ArgumentNullException(nameof(gridView));
                }
                if (dispatcher == null)
                {
                    DebugLogger.LogDebug("ERROR: dispatcher is null");
                    throw new ArgumentNullException(nameof(dispatcher));
                }

                DebugLogger.LogDebug("Parameters validated, finding ScrollViewer...");

                // STEP 1: Scroll to top (prevents crash when unbinding at bottom of list)
                // Must find ScrollViewer on UI thread
                ScrollViewer? scrollViewer = null;
                var tcsFindScroll = new TaskCompletionSource<bool>();
                dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        scrollViewer = FindScrollViewer(gridView);
                        DebugLogger.LogDebug($"ScrollViewer found: {scrollViewer != null}");
                        tcsFindScroll.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcsFindScroll.SetException(ex);
                    }
                });
                await tcsFindScroll.Task;

                if (scrollViewer != null)
                {
                    var tcsScroll = new TaskCompletionSource<bool>();
                    dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            scrollViewer.ChangeView(null, 0, null, true); // Instant scroll to top
                            DebugLogger.LogDebug("Scrolled to top");
                            tcsScroll.SetResult(true);
                        }
                        catch (Exception ex)
                        {
                            tcsScroll.SetException(ex);
                        }
                    });
                    await tcsScroll.Task;
                    await Task.Delay(100); // Wait for scroll to complete
                }

                // STEP 2: Unbind GridView from collection (prevents crashes during reset)
                var tcsUnbind = new TaskCompletionSource<bool>();
                dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        gridView.ItemsSource = null;
                        DebugLogger.LogDebug("Unbound GridView ItemsSource");
                        tcsUnbind.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcsUnbind.SetException(ex);
                    }
                });
                await tcsUnbind.Task;
                await Task.Delay(100); // Wait for unbind to take effect

                // STEP 3: Reset all items to clean state
                // CRITICAL: This must run on UI thread since items may be bound to UI
                DebugLogger.LogDebug($"Resetting items for {newLanguage}");
                var tcsReset = new TaskCompletionSource<bool>();
                dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        foreach (var item in items)
                        {
                            item.IconUri = ImageLoadingHelper.GetNoIconPath();
                            item.ClearLoadingState();
                        }
                        DebugLogger.LogDebug($"Reset {items.Count()} items to no_icon");
                        tcsReset.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogDebug($"Error during item reset: {ex.Message}");
                        tcsReset.SetException(ex);
                    }
                });
                await tcsReset.Task;
                await Task.Delay(100); // Wait for reset to propagate

                // STEP 4: Rebind GridView (forces container recreation)
                // CRITICAL: Rebind to original collection, NOT a copy, to maintain ObservableCollection binding
                var tcsRebind = new TaskCompletionSource<bool>();
                dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        gridView.ItemsSource = items;
                        DebugLogger.LogDebug("Rebound GridView ItemsSource - containers will recreate");
                        tcsRebind.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcsRebind.SetException(ex);
                    }
                });
                await tcsRebind.Task;

                DebugLogger.LogDebug($"CLEAN SLATE language switch complete. ContainerContentChanging will load images on-demand.");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error during CLEAN SLATE language switch: {ex.GetType().Name}: {ex.Message}");
                DebugLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    DebugLogger.LogDebug($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                throw;
            }
        }

        /// <summary>
        /// Finds the ScrollViewer within a DependencyObject hierarchy.
        /// </summary>
        /// <param name="root">The root object to search from.</param>
        /// <returns>The ScrollViewer if found, null otherwise.</returns>
        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer sv)
                return sv;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (FindScrollViewer(child) is ScrollViewer result)
                    return result;
            }

            return null;
        }
    }
}
