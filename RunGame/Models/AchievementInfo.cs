using System;
using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace RunGame.Models
{
    public class AchievementInfo : INotifyPropertyChanged
    {
        private bool _isAchieved;
        private bool _desiredAchieved;
        private int _counter = -1;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string EnglishName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string EnglishDescription { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the state Steam actually holds right now. This is what the read-only status
        /// column shows and what the icon follows — never what the row's checkbox edits.
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
                    OnPropertyChanged(nameof(IsLockVisible));
                    OnPropertyChanged(nameof(IsModified));
                    OnPropertyChanged(nameof(StatusText));
                    // Clear cached icon so it will be reloaded with the correct state
                    IconImage = null;
                }
            }
        }

        /// <summary>
        /// Gets or sets the state the user wants this achievement to be in — the value the row's
        /// checkbox edits. It starts equal to <see cref="IsAchieved"/>; Store writes every row where
        /// the two differ and then brings <see cref="IsAchieved"/> up to date. Staging deliberately
        /// does not touch the icon or the status column, so the row keeps showing what is real until
        /// the change is actually committed.
        /// </summary>
        public bool DesiredAchieved
        {
            get => _desiredAchieved;
            set
            {
                if (_desiredAchieved != value)
                {
                    _desiredAchieved = value;
                    OnPropertyChanged(nameof(DesiredAchieved));
                    OnPropertyChanged(nameof(IsModified));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        /// <summary>
        /// Discards any staged change, so the checkbox matches what Steam currently holds.
        /// </summary>
        public void ResetStaging() => DesiredAchieved = IsAchieved;

        public DateTime? UnlockTime { get; set; }
        public string IconNormal { get; set; } = string.Empty;
        public string IconLocked { get; set; } = string.Empty;
        public int Permission { get; set; }

        public string IconUrl =>
            IsAchieved
                ? IconNormal
                : string.IsNullOrEmpty(IconLocked) ? IconNormal : IconLocked;

        public bool IsProtected => (Permission & 3) != 0;
        public bool IsNotProtected => !IsProtected;

        /// <summary>
        /// Gets whether the lock icon should be visible (protected and not yet achieved).
        /// </summary>
        public bool IsLockVisible => IsProtected && !IsAchieved;

        /// <summary>
        /// Gets whether the checkbox stages a change that Store has not written yet.
        /// </summary>
        public bool IsModified => DesiredAchieved != IsAchieved;

        /// <summary>
        /// Gets the read-only status column text: what Steam holds now, and — when a change is
        /// staged — what Store would change it to.
        /// </summary>
        public string StatusText => IsModified
            ? (IsAchieved ? "Unlocked → Locked" : "Locked → Unlocked")
            : (IsAchieved ? "Unlocked" : "Locked");

        private Bitmap? _iconImage;

        public Bitmap? IconImage
        {
            get => _iconImage;
            set
            {
                if (_iconImage != value)
                {
                    var previous = _iconImage;
                    _iconImage = value;
                    OnPropertyChanged(nameof(IconImage));
                    // Dispose AFTER raising PropertyChanged so any binding has switched
                    // its source to the new bitmap before we release the old one.
                    previous?.Dispose();
                }
            }
        }

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

        public bool IsTimerActive => ScheduledUnlockTime.HasValue && ScheduledUnlockTime > DateTime.Now && !IsAchieved;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
