using System;
using System.ComponentModel;

namespace RunGame.Models
{
    public abstract class StatInfo : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsIncrementOnly { get; set; }
        public int Permission { get; set; }
        public bool IsProtected => (Permission & 3) != 0;
        public bool IsNotProtected => !IsProtected;
        public abstract object Value { get; set; }
        public abstract bool IsModified { get; }
        public abstract string Extra { get; }

        public event PropertyChangedEventHandler? PropertyChanged;
        
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class IntStatInfo : StatInfo
    {
        private int _intValue;

        public int IntValue
        {
            get => _intValue;
            set
            {
                if (_intValue != value)
                {
                    _intValue = value;
                    OnPropertyChanged(nameof(IntValue));
                    OnPropertyChanged(nameof(Value));
                    OnPropertyChanged(nameof(IsModified));
                }
            }
        }

        public int OriginalValue { get; set; }
        public int MinValue { get; set; } = int.MinValue;
        public int MaxValue { get; set; } = int.MaxValue;
        public int MaxChange { get; set; } = 0;

        public override object Value
        {
            get => IntValue;
            set => IntValue = Convert.ToInt32(value);
        }

        public override bool IsModified => IntValue != OriginalValue;

        public override string Extra => $"Original: {OriginalValue}";
    }

    public class FloatStatInfo : StatInfo
    {
        private float _floatValue;

        public float FloatValue
        {
            get => _floatValue;
            set
            {
                if (Math.Abs(_floatValue - value) > float.Epsilon)
                {
                    _floatValue = value;
                    OnPropertyChanged(nameof(FloatValue));
                    OnPropertyChanged(nameof(Value));
                    OnPropertyChanged(nameof(IsModified));
                }
            }
        }

        public float OriginalValue { get; set; }
        public float MinValue { get; set; } = float.MinValue;
        public float MaxValue { get; set; } = float.MaxValue;
        public float MaxChange { get; set; } = 0.0f;

        public override object Value
        {
            get => FloatValue;
            set => FloatValue = Convert.ToSingle(value);
        }

        public override bool IsModified => Math.Abs(FloatValue - OriginalValue) > float.Epsilon;

        public override string Extra => $"Original: {OriginalValue:F2}";
    }
}
