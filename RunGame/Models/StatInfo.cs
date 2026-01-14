using System;
using System.ComponentModel;

namespace RunGame.Models
{
    /// <summary>
    /// Base class for runtime statistic information retrieved from Steam.
    /// Represents a stat's current value, original value, and validation constraints.
    /// Implements INotifyPropertyChanged for WinUI 3 data binding.
    /// </summary>
    public abstract class StatInfo : INotifyPropertyChanged
    {
        /// <summary>
        /// Gets or sets the unique identifier for this statistic.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the localized display name shown in the UI.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this statistic can only be incremented, never decreased.
        /// </summary>
        public bool IsIncrementOnly { get; set; }

        /// <summary>
        /// Gets or sets the permission flags for this statistic.
        /// Non-zero values (permission & 3) indicate protected stats.
        /// </summary>
        public int Permission { get; set; }

        /// <summary>
        /// Gets a value indicating whether this statistic is protected from modification.
        /// Protected stats are typically set by the game server and cannot be edited by users.
        /// </summary>
        public bool IsProtected => (Permission & 3) != 0;

        /// <summary>
        /// Gets a value indicating whether this statistic can be modified by the user.
        /// </summary>
        public bool IsNotProtected => !IsProtected;

        /// <summary>
        /// Gets or sets the current value of this statistic as an object.
        /// Use derived classes (IntStatInfo, FloatStatInfo) for type-safe access.
        /// </summary>
        public abstract object Value { get; set; }

        /// <summary>
        /// Gets a value indicating whether the current value differs from the original value retrieved from Steam.
        /// </summary>
        public abstract bool IsModified { get; }

        /// <summary>
        /// Gets additional information displayed in the UI (typically shows the original value).
        /// </summary>
        public abstract string Extra { get; }

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

    /// <summary>
    /// Represents an integer-based statistic with current and original values.
    /// Includes validation constraints (min/max/max change) from the stat definition.
    /// </summary>
    public class IntStatInfo : StatInfo
    {
        private int _intValue;

        /// <summary>
        /// Gets or sets the current integer value of this statistic.
        /// Notifies UI of changes to IntValue, Value, and IsModified properties.
        /// </summary>
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

        /// <summary>
        /// Gets or sets the original value retrieved from Steam (used to track modifications).
        /// </summary>
        public int OriginalValue { get; set; }

        /// <summary>
        /// Gets or sets the minimum allowed value for this statistic.
        /// </summary>
        public int MinValue { get; set; } = int.MinValue;

        /// <summary>
        /// Gets or sets the maximum allowed value for this statistic.
        /// </summary>
        public int MaxValue { get; set; } = int.MaxValue;

        /// <summary>
        /// Gets or sets the maximum change allowed per update (0 = no limit).
        /// </summary>
        public int MaxChange { get; set; } = 0;

        /// <summary>
        /// Gets or sets the current value as an object for polymorphic access.
        /// </summary>
        public override object Value
        {
            get => IntValue;
            set => IntValue = Convert.ToInt32(value);
        }

        /// <summary>
        /// Gets a value indicating whether the current value differs from the original Steam value.
        /// </summary>
        public override bool IsModified => IntValue != OriginalValue;

        /// <summary>
        /// Gets a string representation of the original value for display in the UI.
        /// </summary>
        public override string Extra => $"Original: {OriginalValue}";
    }

    /// <summary>
    /// Represents a floating-point statistic with current and original values.
    /// Includes validation constraints (min/max/max change) from the stat definition.
    /// </summary>
    public class FloatStatInfo : StatInfo
    {
        private float _floatValue;

        /// <summary>
        /// Gets or sets the current floating-point value of this statistic.
        /// Notifies UI of changes to FloatValue, Value, and IsModified properties.
        /// Uses epsilon comparison to handle floating-point precision.
        /// </summary>
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

        /// <summary>
        /// Gets or sets the original value retrieved from Steam (used to track modifications).
        /// </summary>
        public float OriginalValue { get; set; }

        /// <summary>
        /// Gets or sets the minimum allowed value for this statistic.
        /// </summary>
        public float MinValue { get; set; } = float.MinValue;

        /// <summary>
        /// Gets or sets the maximum allowed value for this statistic.
        /// </summary>
        public float MaxValue { get; set; } = float.MaxValue;

        /// <summary>
        /// Gets or sets the maximum change allowed per update (0 = no limit).
        /// </summary>
        public float MaxChange { get; set; } = 0.0f;

        /// <summary>
        /// Gets or sets the current value as an object for polymorphic access.
        /// </summary>
        public override object Value
        {
            get => FloatValue;
            set => FloatValue = Convert.ToSingle(value);
        }

        /// <summary>
        /// Gets a value indicating whether the current value differs from the original Steam value.
        /// Uses epsilon comparison to handle floating-point precision.
        /// </summary>
        public override bool IsModified => Math.Abs(FloatValue - OriginalValue) > float.Epsilon;

        /// <summary>
        /// Gets a string representation of the original value for display in the UI (formatted to 2 decimal places).
        /// </summary>
        public override string Extra => $"Original: {OriginalValue:F2}";
    }
}
