using System;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RunGame.Models
{
    /// <summary>
    /// Represents runtime information for a Steam achievement, including current state and UI bindings.
    /// Implements INotifyPropertyChanged for WPF/WinUI data binding.
    /// </summary>
    public class AchievementInfo : INotifyPropertyChanged
    {
        private bool _isAchieved;
        private int _counter = -1;

        /// <summary>
        /// Gets or sets the unique identifier for this achievement.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the localized display name of the achievement.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the English display name of the achievement for search purposes.
        /// </summary>
        public string EnglishName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the localized description of the achievement.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the English description of the achievement for search purposes.
        /// </summary>
        public string EnglishDescription { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the original achievement state when loaded from Steam.
        /// Used for change detection to determine if modifications were made.
        /// </summary>
        public bool OriginalIsAchieved { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the achievement is currently unlocked.
        /// Setting this property updates related UI properties and clears the cached icon.
        /// </summary>
        public bool IsAchieved
        {
            get => _isAchieved;
            set
            {
                if (_isAchieved != value)
                {
                    _isAchieved = value;
                    OnPropertyChanged(nameof(IsAchieved));
                    OnPropertyChanged(nameof(IconUrl));
                    OnPropertyChanged(nameof(LockVisibility));
                    // Clear cached icon so it will be reloaded with the correct state
                    IconImage = null;
                }
            }
        }

        /// <summary>
        /// Gets or sets the timestamp when the achievement was unlocked.
        /// Null if the achievement has not been unlocked yet.
        /// </summary>
        public DateTime? UnlockTime { get; set; }

        /// <summary>
        /// Gets or sets the file path to the unlocked/achieved icon image.
        /// </summary>
        public string IconNormal { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the file path to the locked/unachieved icon image.
        /// </summary>
        public string IconLocked { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the permission level for this achievement.
        /// </summary>
        public int Permission { get; set; }

        /// <summary>
        /// Gets the appropriate icon URL based on the current achievement state.
        /// Returns the normal icon if achieved, otherwise returns the locked icon (or normal if no locked icon exists).
        /// </summary>
        public string IconUrl =>
            IsAchieved
                ? IconNormal
                : string.IsNullOrEmpty(IconLocked) ? IconNormal : IconLocked;

        /// <summary>
        /// Gets a value indicating whether this achievement is protected (cannot be modified).
        /// Protection is determined by the lower 2 bits of the Permission field.
        /// </summary>
        public bool IsProtected => (Permission & 3) != 0;

        /// <summary>
        /// Gets a value indicating whether this achievement is not protected (can be modified).
        /// </summary>
        public bool IsNotProtected => !IsProtected;

        /// <summary>
        /// Gets the visibility state for the lock icon overlay.
        /// Visible only when the achievement is protected and not yet achieved.
        /// </summary>
        public Visibility LockVisibility => IsProtected && !IsAchieved ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Gets a value indicating whether the achievement state has been modified from its original value.
        /// </summary>
        public bool IsModified => IsAchieved != OriginalIsAchieved;

        private BitmapSource? _iconImage;

        /// <summary>
        /// Gets or sets the cached icon image for UI display.
        /// Automatically cleared when IsAchieved changes to force reload with correct icon state.
        /// </summary>
        public BitmapSource? IconImage
        {
            get => _iconImage;
            set
            {
                if (_iconImage != value)
                {
                    _iconImage = value;
                    OnPropertyChanged(nameof(IconImage));
                }
            }
        }

        /// <summary>
        /// Gets or sets an internal counter used for achievement management.
        /// Value of -1 indicates no counter is associated with this achievement.
        /// </summary>
        public int Counter
        {
            get => _counter;
            set
            {
                if (_counter != value)
                {
                    _counter = value;
                    OnPropertyChanged(nameof(Counter));
                }
            }
        }

        private DateTime? _scheduledUnlockTime;

        /// <summary>
        /// Gets or sets the scheduled time for automatic achievement unlocking.
        /// Used by the timer service to unlock achievements at specific future times.
        /// </summary>
        public DateTime? ScheduledUnlockTime
        {
            get => _scheduledUnlockTime;
            set
            {
                if (_scheduledUnlockTime != value)
                {
                    _scheduledUnlockTime = value;
                    OnPropertyChanged(nameof(ScheduledUnlockTime));
                    OnPropertyChanged(nameof(IsTimerActive));
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether a timer is currently active for this achievement.
        /// True if there's a scheduled unlock time in the future and the achievement is not yet achieved.
        /// </summary>
        public bool IsTimerActive => ScheduledUnlockTime.HasValue && ScheduledUnlockTime > DateTime.Now && !IsAchieved;

        /// <summary>
        /// Occurs when a property value changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises the PropertyChanged event for the specified property.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
