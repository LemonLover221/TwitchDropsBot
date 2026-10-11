using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Repository;

namespace TwitchDropsBot.Core.Platform.Twitch.Bot;

public static class BadgeScanner
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim SweepLock = new(1, 1);

    private sealed class BadgeState
    {
        public DropCampaign? Campaign;
        public DateTime NextAttemptUtc;
        public int Attempts;
    }

    private static readonly Dictionary<string, BadgeState> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastSweepUtc = DateTime.MinValue;

    private static readonly Regex WatchRx = new(@"\bwatch", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EarnRx =
        new(@"subscri|gift|purchase|cheer|\bbits\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeRx =
        new(@"\d+\s*(?:minute|hour)s?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<List<DropCampaign>> GetBadgeCampaignsAsync(TwitchGqlRepository repository, ILogger logger)
    {
        var campaigns = new List<DropCampaign>();

        try
        {
            if (!await SweepLock.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                Collect(campaigns);
                return campaigns;
            }

            try
            {
                if (DateTime.UtcNow - _lastSweepUtc >= SweepInterval)
                {
                    _lastSweepUtc = DateTime.UtcNow;
                    await SweepAsync(repository, logger);
                }

                Collect(campaigns);
            }
            finally
            {
                SweepLock.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Badge scan failed");
        }

        return campaigns;
    }

    private static async Task SweepAsync(TwitchGqlRepository repository, ILogger logger)
    {
        var badges = await repository.FetchGlobalBadgesAsync();
        if (badges.Count == 0)
        {
            logger.LogWarning("Badge scan: global badge catalog returned no badges");
            return;
        }

        var watchBased = new List<GlobalBadge>();
        foreach (var badge in badges
                     .GroupBy(b => b.SetId, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            if (!WatchRx.IsMatch(badge.Description) || EarnRx.IsMatch(badge.Description))
            {
                continue;
            }

            if (TimeRx.IsMatch(badge.Description)
                || ExtractCategoryNames(badge.Description).Count > 0
                || ExtractChannelHints(badge).Count > 0)
            {
                watchBased.Add(badge);
            }
        }

        int matched = 0, unresolved = 0;

        foreach (var badge in watchBased)
        {
            if (Cache.TryGetValue(badge.SetId, out var known))
            {
                if (known.Campaign != null)
                {
                    matched++;
                    continue;
                }

                if (DateTime.UtcNow < known.NextAttemptUtc)
                {
                    unresolved++;
                    continue;
                }
            }

            DropCampaign? campaign = null;
            string? failure = null;

            try
            {
                campaign = await ResolveBadgeAsync(badge, repository);
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
            }

            var attempts = (known?.Attempts ?? 0) + 1;
            var backoffSeconds = Math.Min(SweepInterval.TotalSeconds * Math.Pow(2, attempts - 1), TimeSpan.FromHours(24).TotalSeconds);

            Cache[badge.SetId] = new BadgeState
            {
                Campaign = campaign,
                Attempts = attempts,
                NextAttemptUtc = DateTime.UtcNow.AddSeconds(backoffSeconds)
            };

            if (campaign != null)
            {
                matched++;
                logger.LogInformation(
                    "Badge scan: '{Title}' -> campaign '{Campaign}' ({Game})",
                    badge.Title, campaign.Name, campaign.Game?.DisplayName ?? campaign.Game?.Name);
            }
            else
            {
                unresolved++;
                logger.LogInformation(
                    "Badge scan: could not resolve '{Title}' ({SetId}): {Description}{Failure}",
                    badge.Title, badge.SetId, badge.Description,
                    failure is null ? "" : $" [{failure}]");
            }
        }

        logger.LogInformation(
            "Badge scan: {Total} global badge(s), {Watch} watch-based, {Matched} resolved to campaign(s), {Unresolved} unresolved (retried next sweep)",
            badges.Count, watchBased.Count, matched, unresolved);
    }

    private static async Task<DropCampaign?> ResolveBadgeAsync(GlobalBadge badge, TwitchGqlRepository repository)
    {
        foreach (var channelLogin in ExtractChannelHints(badge))
        {
            var channelCampaign = await TryResolveViaChannelAsync(channelLogin, badge, repository);
            if (channelCampaign != null)
            {
                return channelCampaign;
            }
        }

        var candidateNames = ExtractCategoryNames(badge.Description);

        foreach (var query in BuildSearchQueries(badge, candidateNames))
        {
            if (candidateNames.Count >= 8)
            {
                break;
            }

            foreach (var category in await repository.SearchCategoriesAsync(query))
            {
                if (string.IsNullOrEmpty(category.Name))
                {
                    continue;
                }

                if (!candidateNames.Contains(category.Name!, StringComparer.OrdinalIgnoreCase))
                {
                    candidateNames.Add(category.Name!);
                }
            }
        }

        foreach (var name in candidateNames.Take(8))
        {
            var slugGame = await repository.FetchGameSlugAsync(name);
            if (string.IsNullOrEmpty(slugGame?.Slug))
            {
                continue;
            }

            var campaign = await TryResolveViaCategoryAsync(slugGame!.Slug!, badge, repository);
            if (campaign != null)
            {
                return campaign;
            }
        }

        return null;
    }

    private static async Task<DropCampaign?> TryResolveViaChannelAsync(
        string channelLogin, GlobalBadge badge, TwitchGqlRepository repository)
    {
        var users = await repository.FetchStreamInformationAsync(new[] { channelLogin });
        var user = users?.FirstOrDefault();
        if (user is null || string.IsNullOrEmpty(user.Id))
        {
            return null;
        }

        var campaigns = await repository.FetchAvailableDropsAsync(user.Id);
        var match = MatchBadge(campaigns, badge);
        if (match is null || match.Game is null || string.IsNullOrEmpty(match.Game.Name))
        {
            return null;
        }

        match.Game.DisplayName ??= match.Game.Name;
        var slugGame = await repository.FetchGameSlugAsync(match.Game.Name);
        if (!string.IsNullOrEmpty(slugGame?.Slug))
        {
            match.Game.Slug = slugGame!.Slug;
        }

        return match;
    }

    private static async Task<DropCampaign?> TryResolveViaCategoryAsync(
        string slug, GlobalBadge badge, TwitchGqlRepository repository)
    {
        var game = await repository.FetchDirectoryPageGameAsync(slug, true);
        var edges = game?.Streams?.Edges;
        if (game is null || edges is null || edges.Count == 0)
        {
            return null;
        }

        foreach (var edge in edges.Take(2))
        {
            var broadcaster = edge.Node?.Broadcaster;
            if (broadcaster is null || string.IsNullOrEmpty(broadcaster.Id))
            {
                continue;
            }

            var campaigns = await repository.FetchAvailableDropsAsync(broadcaster.Id);
            var match = MatchBadge(campaigns, badge);
            if (match != null)
            {
                match.Game = game;
                match.Game.DisplayName ??= match.Game.Name;
                return match;
            }
        }

        return null;
    }

    private static DropCampaign? MatchBadge(List<DropCampaign> campaigns, GlobalBadge badge)
    {
        var target = Normalize(badge.SetId);
        var targetTitle = Normalize(badge.Title);
        if (target.Length == 0 && targetTitle.Length == 0)
        {
            return null;
        }

        foreach (var campaign in campaigns)
        {
            foreach (var drop in campaign.TimeBasedDrops ?? new List<TimeBasedDrop>())
            {
                var dropName = Normalize(drop.Name ?? "");
                var benefitName = Normalize(drop.BenefitEdges?.FirstOrDefault()?.Benefit?.Name ?? "");

                if (dropName == target || dropName == targetTitle
                    || (benefitName.Length > 0 && (benefitName == target || benefitName == targetTitle)))
                {
                    return campaign;
                }
            }
        }

        return null;
    }

    private static List<string> ExtractCategoryNames(string description)
    {
        var names = new List<string>();

        var categoryMatch = Regex.Match(description, @"(?:in|of) the (.+?) category", RegexOptions.IgnoreCase);
        if (categoryMatch.Success)
        {
            names.Add(categoryMatch.Groups[1].Value.Trim());
        }

        var watchForMatch = Regex.Match(
            description, @"watching (?:a streamer in the )?(.+?) for \d+", RegexOptions.IgnoreCase);
        if (watchForMatch.Success)
        {
            names.Add(watchForMatch.Groups[1].Value.Trim());
        }

        var minutesOfMatch = Regex.Match(
            description,
            @"watch(?:ing)? \d+ minutes? of (?:a |an |the )?(.+?)(?: stream| streamer| channel| during|!|\.|$)",
            RegexOptions.IgnoreCase);
        if (minutesOfMatch.Success)
        {
            names.Add(minutesOfMatch.Groups[1].Value.Trim());
        }

        var bareMatch = Regex.Match(description, @"watching (.+?)(?:[.!?]|$)", RegexOptions.IgnoreCase);
        if (bareMatch.Success)
        {
            var bareName = Regex.Replace(
                bareMatch.Groups[1].Value.Trim(), @"\s+on Twitch$", "", RegexOptions.IgnoreCase);
            names.Add(bareName);
        }

        return names
            .Where(name => name.Length is >= 3 and <= 60)
            .Where(name => !Regex.IsMatch(
                name, @"\b(?:clip|vod|any of the|participating|broadcasting|channels?)\b", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractChannelHints(GlobalBadge badge)
    {
        var hints = new List<string>();

        var channelMatch = Regex.Match(badge.Description, @"\s/([A-Za-z0-9_]{3,25})\b");
        if (channelMatch.Success)
        {
            hints.Add(channelMatch.Groups[1].Value);
        }

        if (!string.IsNullOrEmpty(badge.ClickUrl))
        {
            var clickMatch = Regex.Match(
                badge.ClickUrl,
                @"^https?://(?:www\.)?twitch\.tv/([A-Za-z0-9_]{3,25})/?$",
                RegexOptions.IgnoreCase);
            if (clickMatch.Success)
            {
                hints.Add(clickMatch.Groups[1].Value);
            }
        }

        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildSearchQueries(GlobalBadge badge, List<string> parsedNames)
    {
        var queries = new List<string>();

        foreach (var name in parsedNames)
        {
            queries.Add(name);

            var colonIndex = name.IndexOf(':');
            if (colonIndex > 0)
            {
                queries.Add(name[..colonIndex].Trim());
            }
        }

        var titleSplit = Regex.Replace(badge.Title, "(?<=[a-z])(?=[A-Z])", " ")
            .Replace('-', ' ').Replace('_', ' ').Trim();
        if (titleSplit.Length >= 3)
        {
            queries.Add(titleSplit);
        }

        return queries
            .Where(query => !string.IsNullOrWhiteSpace(query) && query.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static void Collect(List<DropCampaign> into)
    {
        foreach (var state in Cache.Values)
        {
            if (state.Campaign != null)
            {
                into.Add(CloneForAccount(state.Campaign));
            }
        }
    }

    private static string Normalize(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static DropCampaign CloneForAccount(DropCampaign campaign) => new()
    {
        Id = campaign.Id,
        Name = campaign.Name,
        Status = campaign.Status,
        StartAt = campaign.StartAt,
        EndAt = campaign.EndAt,
        EndsAt = campaign.EndsAt,
        DetailsURL = campaign.DetailsURL,
        AccountLinkURL = campaign.AccountLinkURL,
        Summary = campaign.Summary,
        Game = campaign.Game,
        Allow = campaign.Allow,
        BadgeScanDiscovered = true,
        TimeBasedDrops = campaign.TimeBasedDrops.Select(drop => new TimeBasedDrop
        {
            Id = drop.Id,
            Name = drop.Name,
            StartAt = drop.StartAt,
            EndAt = drop.EndAt,
            RequiredMinutesWatched = drop.RequiredMinutesWatched,
            RequiredSubs = drop.RequiredSubs,
            BenefitEdges = (drop.BenefitEdges ?? new List<DropBenefitEdge>()).Select(edge => new DropBenefitEdge
            {
                EntitlementLimit = edge.EntitlementLimit,
                Benefit = edge.Benefit is null
                    ? null
                    : new DropBenefit
                    {
                        Id = edge.Benefit.Id,
                        Name = edge.Benefit.Name,
                        ImageAssetURL = edge.Benefit.ImageAssetURL,
                        Game = edge.Benefit.Game
                    }
            }).ToList()
        }).ToList()
    };
}
