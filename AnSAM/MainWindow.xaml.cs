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
        // 方案A：卡片根節點 DoubleTapped
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
                StatusText.Text = $"Search：{keyword}";
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
            // TODO: 使用者從建議清單選了一項時的處理
        }

    }
    public class GameItem
    {
        public string Title { get; }
        public Uri Cover { get; }
        public int ID { get; set;  }
        public BitmapIcon? CoverIcon { get; set; }

        // 啟動資訊（擇一或皆可）
        public string? ExePath { get; }
        public string? Arguments { get; }
        public string? UriScheme { get; }

        public GameItem(string title, int ID, BitmapIcon bitmapIcon ) 
        {
            // TODO: Initialize properties
        }
    }
}
