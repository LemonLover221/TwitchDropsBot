using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;

namespace TwitchDropsBot.Core.Platform.Twitch.Bot;

public static class CampaignRewardPriority
{
    public const int BadgeTier = 0;
    public const int EmoteTier = 1;
    public const int RewardTier = 2;
    public const int RegularTier = 3;

    public static int GetRewardTier(AbstractCampaign campaign)
    {
        if (campaign is DropCampaign badgeScanCampaign && badgeScanCampaign.BadgeScanDiscovered)
        {
            return badgeScanCampaign.ScanTier;
        }

        if (campaign is RewardCampaign rewardCampaign)
        {
            // Reward campaigns (chat badges, capes, ...) don't carry benefit types.
            // Watch-based ones are the collectible events worth prioritizing;
            // sub-based ones are filtered out earlier anyway.
            return (rewardCampaign.UnlockRequirements?.MinuteWatchedGoal ?? 0) > 0
                ? RewardTier
                : RegularTier;
        }

        var hasBadge = false;
        var hasEmote = false;

        foreach (var drop in campaign.TimeBasedDrops ?? new List<TimeBasedDrop>())
        {
            foreach (var edge in drop.BenefitEdges ?? new List<DropBenefitEdge>())
            {
                switch (edge.Benefit?.DistributionType)
                {
                    case DistributionType.BADGE:
                        hasBadge = true;
                        break;
                    case DistributionType.EMOTE:
                        hasEmote = true;
                        break;
                }
            }
        }

        if (hasBadge)
            return BadgeTier;

        if (hasEmote)
            return EmoteTier;

        return RegularTier;
    }
}
