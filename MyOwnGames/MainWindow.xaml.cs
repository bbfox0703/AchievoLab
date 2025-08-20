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
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyOwnGames
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<GameEntry> GameItems { get; } = new();

        private string _statusText = "Ready.";
        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                    // Toggle progress visibility
                    ProgressVisibility = value ? Visibility.Visible : Visibility.Collapsed;
                    if (value) ProgressPercentText = ""; // when indeterminate
                }
            }
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set { if (Math.Abs(_progressValue - value) > double.Epsilon) { _progressValue = value; OnPropertyChanged(); ProgressPercentText = $"{(int)_progressValue}%"; } }
        }

        private Visibility _progressVisibility = Visibility.Collapsed;
        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            set { if (_progressVisibility != value) { _progressVisibility = value; OnPropertyChanged(); } }
        }

        private string _progressPercentText = "";
        public string ProgressPercentText
        {
            get => _progressPercentText;
            set { if (_progressPercentText != value) { _progressPercentText = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public MainWindow()
        {
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true; // 可選：讓標題列吃主題
            this.AppWindow.Title = "Steam Games (WinUI 3)";

            // 綁定 DataContext
            //this.DataContext = this;

            // Demo 預設資料（可移除）
            SeedDemoRows();
        }
        private void SeedDemoRows()
        {
            GameItems.Add(new GameEntry
            {
                AppId = 570,
                IconUri = "ms-appx:///Assets/steam_placeholder.png", // 請把圖放在專案 Assets
                NameEn = "Dota 2",
                NameLocalized = "Dota 2" // Demo：暫時同英文
            });

            GameItems.Add(new GameEntry
            {
                AppId = 730,
                IconUri = "ms-appx:///Assets/steam_placeholder.png",
                NameEn = "Counter-Strike 2",
                NameLocalized = "Counter-Strike 2"
            });
        }

        private async void GetGamesButton_Click(object sender, RoutedEventArgs e)
        {
            var apiKey = ApiKeyBox.Text?.Trim();
            var steamId64 = SteamIdBox.Text?.Trim();

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(steamId64))
            {
                StatusText = "請輸入 Steam API Key 與 SteamID_64。";
                return;
            }

            try
            {
                IsLoading = true;
                StatusText = "Fetching game list from Steam Web API...";
                ProgressValue = 0;

                GameItems.Clear();

                // ↓↓↓ TODO：在這裡呼叫你的 Steam Web API。
                // 建議流程：
                // 1) IPlayerService/GetOwnedGames 取得擁有清單（含 appid、playtime 等）
                // 2) ISteamApps/GetAppList/v2 或第三方資料源，對 appid → 英文名稱/多語名稱映射
                // 3) 依 OS 目前文化（CultureInfo.CurrentUICulture）挑對應語系名稱
                //
                // 下面用假的進度與資料模擬：

                var totalSteps = 10;
                for (int i = 1; i <= totalSteps; i++)
                {
                    await Task.Delay(120); // 模擬網路/處理
                    ProgressValue = i * (100.0 / totalSteps);
                }

                // 依 OS 文化設定（示例）
                var osCulture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName; // "zh", "ja", "en", ...
                // 假資料：實際上請替換成 API 回傳
                GameItems.Add(new GameEntry
                {
                    AppId = 1091500,
                    IconUri = "ms-appx:///Assets/steam_placeholder.png",
                    NameEn = "Cyberpunk 2077",
                    NameLocalized = osCulture == "zh" ? "電馭叛客 2077" : "Cyberpunk 2077"
                });

                GameItems.Add(new GameEntry
                {
                    AppId = 1174180,
                    IconUri = "ms-appx:///Assets/steam_placeholder.png",
                    NameEn = "Red Dead Redemption 2",
                    NameLocalized = osCulture == "zh" ? "碧血狂殺 2" : "Red Dead Redemption 2"
                });

                StatusText = $"完成，共 {GameItems.Count} 筆。";
            }
            catch (Exception ex)
            {
                StatusText = "發生錯誤：" + ex.Message;
            }
            finally
            {
                IsLoading = false;
                ProgressValue = 0;
            }
        }

        private void KeywordBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var keyword = args.QueryText?.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                StatusText = "請輸入關鍵字。";
                return;
            }

            // 這裡你可以依 keyword 做 client-side 篩選，或觸發伺服端查詢
            StatusText = $"搜尋關鍵字：{keyword}";
            // TODO: 可對 GameItems 做過濾（或在資料來源端處理）
        }

        private void KeywordBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // 可選：提供建議清單（AutoSuggest）；此處省略
        }
    }

    public class GameEntry
    {
        public int AppId { get; set; }
        public string IconUri { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string NameLocalized { get; set; } = "";
    }
}

