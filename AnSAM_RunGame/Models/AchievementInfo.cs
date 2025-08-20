using System;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnSAM.RunGame.Models
{
    public class AchievementInfo : INotifyPropertyChanged
    {
        private bool _isAchieved;
        private int _counter = -1;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        
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
                    // Clear cached icon so it will be reloaded with the correct state
                    IconImage = null;
                }
            } 
        }
        
        public DateTime? UnlockTime { get; set; }
        public string IconNormal { get; set; } = string.Empty;
        public string IconLocked { get; set; } = string.Empty;
        public int Permission { get; set; }
        public string IconUrl => IsAchieved ? IconNormal : IconLocked;
        public bool IsProtected => (Permission & 3) != 0;
        
        // Cached icon image
        private BitmapImage? _iconImage;
        public BitmapImage? IconImage
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
        
        // Timer properties for real-time achievement unlocking
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