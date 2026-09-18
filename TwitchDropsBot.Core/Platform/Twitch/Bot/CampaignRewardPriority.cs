using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;

namespace TwitchDropsBot.Core.Platform.Twitch.Bot;

public static class CampaignRewardPriority
{
    public const int BadgeTier = 0;
    public const int EmoteTier = 1;
    public const int RegularTier = 2;

    public static int GetRewardTier(AbstractCampaign campaign)
    {
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
