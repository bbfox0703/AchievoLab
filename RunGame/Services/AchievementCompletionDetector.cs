using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RunGame.Models;

namespace RunGame.Services
{
    /// <summary>
    /// Detects "completionist" achievements — the meta achievement a game grants for
    /// unlocking every other achievement (e.g. "Unlock all achievements", "獲得所有成就",
    /// "取得所有獎盃"). Used by the completionist-protection guard so this achievement is
    /// not unlocked before all of the others are.
    /// </summary>
    /// <remarks>
    /// Matching is intentionally forgiving: both the localized (<see cref="AchievementInfo.Name"/>,
    /// <see cref="AchievementInfo.Description"/>) and the always-populated English
    /// (<see cref="AchievementInfo.EnglishName"/>, <see cref="AchievementInfo.EnglishDescription"/>)
    /// fields are checked, so the achievement is still recognized regardless of the currently
    /// selected display language.
    /// </remarks>
    public static partial class AchievementCompletionDetector
    {
        // English: "unlock/earn/obtain/collect all|every (of the) (<= 3 words) achievement(s)".
        // The bounded word gap covers phrasings like "Earn all base game achievements" while
        // keeping the pattern from swallowing whole sentences.
        [GeneratedRegex(
            @"\b(?:unlock|earn|obtain|collect)\s+(?:all|every)\s+(?:of\s+the\s+)?(?:\S+\s+){0,3}achievements?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex AllAchievementsEnglishRegex();

        // English trophies — some PlayStation-style ports reuse the trophy wording on Steam.
        [GeneratedRegex(
            @"\b(?:unlock|earn|obtain|collect)\s+(?:all|every)\s+(?:of\s+the\s+)?(?:\S+\s+){0,3}troph(?:y|ies)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex AllTrophiesEnglishRegex();

        // CJK / Korean substrings that unambiguously mean "all achievements / all trophies".
        // Note: 所有 (all) and 成就 (achievement) are identical glyphs in Traditional and
        // Simplified Chinese, so "所有成就" covers 獲得/解鎖/完成/達成/取得 …所有成就 in both.
        private static readonly string[] AllAchievementMarkers =
        {
            // Chinese achievements
            "所有成就",
            "全部成就",
            // Chinese trophies (zh-Hant / zh-Hans / mixed forms)
            "所有獎盃", "所有奖杯", "所有獎杯",
            "全部獎盃", "全部奖杯", "全部獎杯",
            // Japanese
            "全実績", "すべての実績", "全ての実績", "全アチーブメント",
            // Korean
            "모든 도전 과제", "모든 도전과제", "모든 업적",
        };

        /// <summary>
        /// Returns <c>true</c> when the achievement appears to be a game's "unlock everything"
        /// meta achievement, based on its localized or English name/description.
        /// </summary>
        public static bool IsUnlockAllAchievement(AchievementInfo? achievement)
        {
            if (achievement is null) return false;

            return IsUnlockAllText(achievement.EnglishDescription)
                || IsUnlockAllText(achievement.Description)
                || IsUnlockAllText(achievement.EnglishName)
                || IsUnlockAllText(achievement.Name);
        }

        /// <summary>
        /// Returns <c>true</c> when the supplied text describes unlocking every achievement
        /// (or every trophy) in a game.
        /// </summary>
        public static bool IsUnlockAllText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            foreach (var marker in AllAchievementMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                    return true;
            }

            return AllAchievementsEnglishRegex().IsMatch(text)
                || AllTrophiesEnglishRegex().IsMatch(text);
        }

        /// <summary>
        /// From <paramref name="candidates"/>, returns the completionist ("unlock every achievement")
        /// achievements that are NOT yet safe to unlock — i.e. at least one other achievement would
        /// still be locked after the pending operation. Used by the completionist-protection guard.
        /// </summary>
        /// <param name="allAchievements">Every achievement in the game (the full set to check completion against).</param>
        /// <param name="candidates">The achievements about to be unlocked/scheduled.</param>
        /// <param name="willBeUnlocked">Predicate reporting whether a given achievement will end up unlocked.</param>
        /// <param name="maxRemainingLocked">Outputs the largest "still locked" count across the blocked completionists.</param>
        /// <remarks>
        /// Protected achievements are excluded from the "still locked" count: the tool can never toggle
        /// them, so a protected-and-locked achievement must not make the completionist permanently
        /// unreachable. A candidate that is already achieved is never considered.
        /// </remarks>
        public static List<AchievementInfo> FindUnsafeCompletionists(
            IEnumerable<AchievementInfo> allAchievements,
            IEnumerable<AchievementInfo> candidates,
            Func<AchievementInfo, bool> willBeUnlocked,
            out int maxRemainingLocked)
        {
            var allList = allAchievements as IReadOnlyList<AchievementInfo> ?? allAchievements.ToList();
            var result = new List<AchievementInfo>();
            int maxRemaining = 0;

            foreach (var candidate in candidates.Where(a => !a.IsAchieved && IsUnlockAllAchievement(a)))
            {
                int remaining = 0;
                foreach (var other in allList)
                {
                    if (ReferenceEquals(other, candidate)) continue;
                    if (other.IsProtected) continue; // cannot be toggled by the tool
                    if (!willBeUnlocked(other)) remaining++;
                }

                if (remaining > 0)
                {
                    result.Add(candidate);
                    if (remaining > maxRemaining) maxRemaining = remaining;
                }
            }

            maxRemainingLocked = maxRemaining;
            return result;
        }
    }
}
