namespace TwitchDropsBot.Core.Platform.Twitch.Models;

public partial class DropCampaign
{
    public bool BadgeScanDiscovered { get; set; }

    public int ScanTier { get; set; }

    public bool InventoryDiscovered { get; set; }

    public BlindWatchInfo? BlindWatch { get; set; }

    public sealed record BlindWatchInfo(string SetId, string Title, string Slug, int Minutes);
}
