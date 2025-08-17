using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AnSAM
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<GameItem> Games { get; } = new();
        public MainWindow()
        {
            InitializeComponent();
        }

        private void GameCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // TODO: Call SAM.Game.StartGame() or similar method
        }

        private void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Refresh";
            StatusProgress.Value = 0;
            StatusExtra.Text = "0%";
            // TODO: Refresh game list or other items
        }

        //private void OnSearchClicked(object sender, RoutedEventArgs e)
        //{
        //    StatusText.Text = "Filter..";
        //}

        private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            // TODO: Search game titles or other items based on the keyword
            var keyword = args.QueryText?.Trim();
            if (!string.IsNullOrEmpty(keyword))
            {
                StatusText.Text = $"SearchG{keyword}";
            }
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                // TODO: Filter game list via keyword
            }
        }

        private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            // TODO: Handle suggestion chosen
        }

    }

    public class GameItem
    {
        public string Title { get; set; }
        public int ID { get; set; }
        public Uri? CoverUri { get; set; }
        public BitmapIcon? CoverIcon { get; set; }

        public string? ExePath { get; set; }
        public string? Arguments { get; set; }
        public string? UriScheme { get; set; }

        public GameItem(string title,
                         int id,
                         Uri? coverUri = null,
                         string? exePath = null,
                         string? arguments = null,
                         string? uriScheme = null)
        {
            Title = title;
            ID = id;
            CoverUri = coverUri;
            ExePath = exePath;
            Arguments = arguments;
            UriScheme = uriScheme;

            if (coverUri != null)
            {
                CoverIcon = new BitmapIcon { UriSource = coverUri };
            }
        }

        public static GameItem FromSteamApp(SteamAppData app)
        {
            Uri? cover = string.IsNullOrEmpty(app.CoverUrl) ? null : new Uri(app.CoverUrl, UriKind.RelativeOrAbsolute);
            return new GameItem(app.Title,
                                app.AppId,
                                cover,
                                app.ExePath,
                                app.Arguments,
                                app.UriScheme);
        }
    }
}
