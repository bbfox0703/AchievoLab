namespace RunGame.Models
{
    public abstract class StatDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IncrementOnly { get; set; }
        public int Permission { get; set; }
    }

    public class IntegerStatDefinition : StatDefinition
    {
        public int MinValue { get; set; } = int.MinValue;
        public int MaxValue { get; set; } = int.MaxValue;
        public int MaxChange { get; set; } = 0;
        public bool SetByTrustedGameServer { get; set; }
        public int DefaultValue { get; set; } = 0;
    }

    public class FloatStatDefinition : StatDefinition
    {
        public float MinValue { get; set; } = float.MinValue;
        public float MaxValue { get; set; } = float.MaxValue;
        public float MaxChange { get; set; } = 0.0f;
        public float DefaultValue { get; set; } = 0.0f;
    }
}
