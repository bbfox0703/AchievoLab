using RunGame.Models;
using RunGame.Services;
using Xunit;

namespace RunGame.Tests
{
    public class AchievementCompletionDetectorTests
    {
        [Theory]
        // English variants explicitly requested plus close phrasings.
        [InlineData("Unlock every achievement")]
        [InlineData("Unlock all achievements")]
        [InlineData("Unlock all of the achievements")]
        [InlineData("Earn all achievements")]
        [InlineData("Earn all base game achievements")]
        [InlineData("Earn all 50 achievements")]
        [InlineData("EARN ALL ACHIEVEMENTS")]
        [InlineData("Obtain all achievements in the game")]
        [InlineData("Collect every achievement")]
        // Trophy wording (PlayStation-style ports).
        [InlineData("Unlock all trophies")]
        [InlineData("Earn every trophy")]
        // Traditional Chinese (requested).
        [InlineData("獲得所有成就")]
        [InlineData("解鎖所有成就")]
        [InlineData("取得所有獎盃")]
        // Simplified Chinese equivalents.
        [InlineData("获得所有成就")]
        [InlineData("解锁所有成就")]
        // Japanese / Korean.
        [InlineData("全実績を解除する")]
        [InlineData("모든 도전 과제 달성")]
        public void IsUnlockAllText_MatchesCompletionistPhrases(string text)
        {
            Assert.True(AchievementCompletionDetector.IsUnlockAllText(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("Unlock the first achievement")]
        [InlineData("Win 100 matches")]
        [InlineData("Reach level 50")]
        [InlineData("Defeat the final boss")]
        [InlineData("Collect all the coins in world 1")]
        [InlineData("Earn a gold medal")]
        public void IsUnlockAllText_DoesNotMatchOrdinaryPhrases(string? text)
        {
            Assert.False(AchievementCompletionDetector.IsUnlockAllText(text));
        }

        [Fact]
        public void IsUnlockAllAchievement_UsesEnglishDescriptionRegardlessOfDisplayLanguage()
        {
            // Display language is Japanese but the always-populated English fields still match.
            var achievement = new AchievementInfo
            {
                Id = "COMPLETIONIST",
                Name = "全実績解除",
                Description = "ゲーム内のロック済みでない実績はありません",
                EnglishName = "Completionist",
                EnglishDescription = "Unlock all achievements"
            };

            Assert.True(AchievementCompletionDetector.IsUnlockAllAchievement(achievement));
        }

        [Fact]
        public void IsUnlockAllAchievement_ReturnsFalseForOrdinaryAchievement()
        {
            var achievement = new AchievementInfo
            {
                Id = "FIRST_BLOOD",
                Name = "First Blood",
                Description = "Get your first kill",
                EnglishName = "First Blood",
                EnglishDescription = "Get your first kill"
            };

            Assert.False(AchievementCompletionDetector.IsUnlockAllAchievement(achievement));
        }

        [Fact]
        public void IsUnlockAllAchievement_ReturnsFalseForNull()
        {
            Assert.False(AchievementCompletionDetector.IsUnlockAllAchievement(null));
        }

        // ---- FindUnsafeCompletionists (completionist-protection guard core) ----

        private static AchievementInfo Ach(string id, bool achieved = false, int permission = 0, string? desc = null)
            => new AchievementInfo
            {
                Id = id,
                Name = id,
                EnglishName = id,
                Description = desc ?? id,
                EnglishDescription = desc ?? id,
                IsAchieved = achieved,
                Permission = permission
            };

        private static readonly System.Func<AchievementInfo, bool> ByAchievedState = a => a.IsAchieved;

        [Fact]
        public void FindUnsafeCompletionists_BlocksCompletionist_WhenOthersStillLocked()
        {
            var completionist = Ach("ALL", desc: "Unlock all achievements");
            var all = new[] { completionist, Ach("A"), Ach("B", achieved: true) };

            var blocked = AchievementCompletionDetector.FindUnsafeCompletionists(
                all, new[] { completionist }, ByAchievedState, out int remaining);

            Assert.Single(blocked);
            Assert.Same(completionist, blocked[0]);
            Assert.Equal(1, remaining); // only "A" is still locked and unprotected
        }

        [Fact]
        public void FindUnsafeCompletionists_AllowsCompletionist_WhenAllOthersWillBeUnlocked()
        {
            var completionist = Ach("ALL", desc: "Unlock all achievements");
            var a = Ach("A");
            var b = Ach("B", achieved: true);
            var all = new[] { completionist, a, b };

            // Predicate: completionist and "A" are in the pending unlock batch; "B" already achieved.
            var batch = new System.Collections.Generic.HashSet<AchievementInfo> { completionist, a };
            System.Func<AchievementInfo, bool> willBeUnlocked = x => x.IsAchieved || batch.Contains(x);

            var blocked = AchievementCompletionDetector.FindUnsafeCompletionists(
                all, new[] { completionist }, willBeUnlocked, out int remaining);

            Assert.Empty(blocked);
            Assert.Equal(0, remaining);
        }

        [Fact]
        public void FindUnsafeCompletionists_IgnoresProtectedLockedAchievements()
        {
            // Regression for the review finding: a protected-locked achievement can never be toggled,
            // so it must NOT keep the completionist permanently blocked.
            var completionist = Ach("ALL", desc: "Unlock all achievements");
            var protectedLocked = Ach("PROT", achieved: false, permission: 3);
            var all = new[] { completionist, protectedLocked, Ach("B", achieved: true) };

            var blocked = AchievementCompletionDetector.FindUnsafeCompletionists(
                all, new[] { completionist }, ByAchievedState, out int remaining);

            Assert.Empty(blocked);
            Assert.Equal(0, remaining);
        }

        [Fact]
        public void FindUnsafeCompletionists_IgnoresAlreadyAchievedCompletionist()
        {
            var completionist = Ach("ALL", achieved: true, desc: "Unlock all achievements");
            var all = new[] { completionist, Ach("A") };

            var blocked = AchievementCompletionDetector.FindUnsafeCompletionists(
                all, new[] { completionist }, ByAchievedState, out int remaining);

            Assert.Empty(blocked);
            Assert.Equal(0, remaining);
        }

        [Fact]
        public void FindUnsafeCompletionists_IgnoresNonCompletionistCandidates()
        {
            var ordinary = Ach("A", desc: "Get your first kill");
            var all = new[] { ordinary, Ach("B") };

            var blocked = AchievementCompletionDetector.FindUnsafeCompletionists(
                all, new[] { ordinary }, ByAchievedState, out int remaining);

            Assert.Empty(blocked);
            Assert.Equal(0, remaining);
        }
    }
}
