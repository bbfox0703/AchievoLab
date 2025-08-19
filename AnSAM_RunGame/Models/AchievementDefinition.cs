namespace AnSAM.RunGame.Models
{
    public class AchievementDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconNormal { get; set; } = string.Empty;
        public string IconLocked { get; set; } = string.Empty;
        public bool IsHidden { get; set; }
        public int Permission { get; set; }

        public override string ToString()
        {
            return $"{Name ?? Id ?? base.ToString()}: {Permission}";
        }
    }
}