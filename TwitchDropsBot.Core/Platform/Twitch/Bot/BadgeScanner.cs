using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Repository;

namespace TwitchDropsBot.Core.Platform.Twitch.Bot;

public static class BadgeScanner
{
    public sealed record BadgeCampaign(DropCampaign Campaign, string SetId);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim SweepLock = new(1, 1);

    private sealed class BadgeState
    {
        public DropCampaign? Campaign;
        public bool NotWatchable;
        public DateTime NextAttemptUtc;
        public int Attempts;
    }

    private sealed record ScanMatch(DropCampaign? Campaign, bool NotWatchable);

    private static readonly Dictionary<string, BadgeState> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DropCampaign> Harvested = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();
    private static DateTime _lastSweepUtc = DateTime.MinValue;

    private static readonly Regex WatchRx =
        new(@"\b(?:watch|support)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EarnRx =
        new(@"subscri|gift|purchase|cheer|\bbits\b|\bsub\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeRx =
        new(@"\d+\s*(?:minute|hour)s?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<BadgeCampaign> GetBadgeCampaigns(
        TwitchGqlRepository repository, ILogger logger, List<string>? scanCategories = null)
    {
        TryStartSweep(repository, logger, scanCategories);

        var campaigns = new List<BadgeCampaign>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (CacheGate)
        {
            foreach (var (setId, state) in Cache)
            {
                if (state.Campaign != null && seen.Add(state.Campaign.Id ?? ""))
                {
                    var clone = CloneForAccount(state.Campaign);
                    clone.ScanTier = CampaignRewardPriority.BadgeTier;
                    campaigns.Add(new BadgeCampaign(clone, setId));
                }
            }

            foreach (var harvested in Harvested.Values)
            {
                if (seen.Add(harvested.Id ?? ""))
                {
                    campaigns.Add(new BadgeCampaign(CloneForAccount(harvested), ""));
                }
            }
        }

        return campaigns;
    }

    private static void TryStartSweep(
        TwitchGqlRepository repository, ILogger logger, List<string>? scanCategories)
    {
        lock (CacheGate)
        {
            if (DateTime.UtcNow - _lastSweepUtc < SweepInterval || !SweepLock.Wait(0))
            {
                return;
            }

            _lastSweepUtc = DateTime.UtcNow;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SweepAsync(repository, logger, scanCategories);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Badge scan failed");
            }
            finally
            {
                SweepLock.Release();
            }
        });
    }

    private static async Task SweepAsync(
        TwitchGqlRepository repository, ILogger logger, List<string>? scanCategories)
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

        int matched = 0, unresolved = 0, deferred = 0;

        foreach (var badge in watchBased)
        {
            BadgeState? known = null;
            lock (CacheGate)
            {
                Cache.TryGetValue(badge.SetId, out known);
            }

            if (known != null)
            {
                if (known.Campaign != null)
                {
                    matched++;
                    continue;
                }

                if (known.NotWatchable)
                {
                    deferred++;
                    continue;
                }

                if (DateTime.UtcNow < known.NextAttemptUtc)
                {
                    unresolved++;
                    continue;
                }
            }

            DropCampaign? campaign = null;
            var notWatchable = false;
            string? failure = null;

            try
            {
                var match = await ResolveBadgeAsync(badge, repository);
                campaign = match.Campaign;
                notWatchable = match.NotWatchable;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
            }

            var attempts = (known?.Attempts ?? 0) + 1;
            var backoffSeconds = Math.Min(
                SweepInterval.TotalSeconds * Math.Pow(2, attempts - 1),
                TimeSpan.FromHours(24).TotalSeconds);

            lock (CacheGate)
            {
                Cache[badge.SetId] = new BadgeState
                {
                    Campaign = campaign,
                    NotWatchable = notWatchable,
                    Attempts = attempts,
                    NextAttemptUtc = DateTime.UtcNow.AddSeconds(backoffSeconds)
                };
            }

            if (campaign != null)
            {
                matched++;
                logger.LogInformation(
                    "Badge scan: '{Title}' -> campaign '{Campaign}' ({Game})",
                    badge.Title, campaign.Name, campaign.Game?.DisplayName ?? campaign.Game?.Name);
            }
            else if (notWatchable)
            {
                deferred++;
                logger.LogInformation(
                    "Badge scan: '{Title}' is not earnable by watching, skipping", badge.Title);
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
            "Badge scan: {Total} global badge(s), {Watch} watch-based, {Matched} resolved to campaign(s), {Unresolved} unresolved, {Deferred} not watchable, {Harvested} harvested campaign(s)",
            badges.Count, watchBased.Count, matched, unresolved, deferred, Harvested.Count);

        if (scanCategories is { Count: > 0 })
        {
            foreach (var categoryName in scanCategories)
            {
                if (string.IsNullOrWhiteSpace(categoryName))
                {
                    continue;
                }

                try
                {
                    var slugGame = await repository.FetchGameSlugAsync(categoryName.Trim());
                    if (string.IsNullOrEmpty(slugGame?.Slug))
                    {
                        logger.LogInformation(
                            "Badge scan: could not resolve scan category '{Category}'", categoryName);
                        continue;
                    }

                    var game = await repository.FetchDirectoryPageGameAsync(slugGame!.Slug!, true);
                    var edges = game?.Streams?.Edges;
                    if (game is null || edges is null || edges.Count == 0)
                    {
                        continue;
                    }

                    foreach (var edge in edges.Take(2))
                    {
                        var broadcaster = edge.Node?.Broadcaster;
                        if (broadcaster is null || string.IsNullOrEmpty(broadcaster.Id))
                        {
                            continue;
                        }

                        var campaigns = await repository.FetchAvailableDropsRawAsync(broadcaster.Id);
                        await HarvestCampaignsAsync(repository, campaigns);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Badge scan: sweep of category '{Category}' failed", categoryName);
                }
            }

            logger.LogInformation(
                "Badge scan: category sweep done, {Harvested} harvested campaign(s) total", Harvested.Count);
        }
    }

    private static async Task<ScanMatch> ResolveBadgeAsync(GlobalBadge badge, TwitchGqlRepository repository)
    {
        foreach (var channelLogin in ExtractChannelHints(badge))
        {
            var channelMatch = await TryResolveViaChannelAsync(channelLogin, badge, repository);
            if (channelMatch.Campaign != null || channelMatch.NotWatchable)
            {
                return channelMatch;
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

            var categoryMatch = await TryResolveViaCategoryAsync(slugGame!.Slug!, badge, repository);
            if (categoryMatch.Campaign != null || categoryMatch.NotWatchable)
            {
                return categoryMatch;
            }
        }

        return new ScanMatch(null, false);
    }

    private static async Task<ScanMatch> TryResolveViaChannelAsync(
        string channelLogin, GlobalBadge badge, TwitchGqlRepository repository)
    {
        var users = await repository.FetchStreamInformationAsync(new[] { channelLogin });
        var user = users?.FirstOrDefault();
        if (user is null || string.IsNullOrEmpty(user.Id))
        {
            return new ScanMatch(null, false);
        }

        var campaigns = await repository.FetchAvailableDropsRawAsync(user.Id);
        await HarvestCampaignsAsync(repository, campaigns);
        var match = MatchBadge(campaigns, badge);
        if (match.Campaign != null)
        {
            if (match.Campaign.Game is null || string.IsNullOrEmpty(match.Campaign.Game.Name))
            {
                return new ScanMatch(null, false);
            }

            match.Campaign.Game.DisplayName ??= match.Campaign.Game.Name;
            var slugGame = await repository.FetchGameSlugAsync(match.Campaign.Game.Name);
            if (string.IsNullOrEmpty(slugGame?.Slug))
            {
                return new ScanMatch(null, false);
            }

            match.Campaign.Game.Slug = slugGame!.Slug;
        }

        return match;
    }

    private static async Task<ScanMatch> TryResolveViaCategoryAsync(
        string slug, GlobalBadge badge, TwitchGqlRepository repository)
    {
        var game = await repository.FetchDirectoryPageGameAsync(slug, true);
        var edges = game?.Streams?.Edges;
        if (game is null || edges is null || edges.Count == 0)
        {
            return new ScanMatch(null, false);
        }

        foreach (var edge in edges.Take(2))
        {
            var broadcaster = edge.Node?.Broadcaster;
            if (broadcaster is null || string.IsNullOrEmpty(broadcaster.Id))
            {
                continue;
            }

            var campaigns = await repository.FetchAvailableDropsRawAsync(broadcaster.Id);
            await HarvestCampaignsAsync(repository, campaigns);
            var match = MatchBadge(campaigns, badge);
            if (match.Campaign != null)
            {
                game.Slug ??= slug;
                match.Campaign!.Game = game;
                match.Campaign.Game.DisplayName ??= match.Campaign.Game.Name;
                return match;
            }

            if (match.NotWatchable)
            {
                return match;
            }
        }

        return new ScanMatch(null, false);
    }

    private static ScanMatch MatchBadge(JsonElement? campaignsElement, GlobalBadge badge)
    {
        if (campaignsElement is not { ValueKind: JsonValueKind.Array } campaigns)
        {
            return new ScanMatch(null, false);
        }

        var target = Normalize(badge.SetId);
        var targetTitle = Normalize(badge.Title);
        if (target.Length == 0 && targetTitle.Length == 0)
        {
            return new ScanMatch(null, false);
        }

        foreach (var campaign in campaigns.EnumerateArray())
        {
            if (!campaign.TryGetProperty("timeBasedDrops", out var dropsElement)
                || dropsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var drop in dropsElement.EnumerateArray())
            {
                var dropName = Normalize(GetString(drop, "name") ?? "");
                var benefitName = Normalize(GetBenefitName(drop) ?? "");

                var matches = dropName == target || dropName == targetTitle
                              || (benefitName.Length > 0 && (benefitName == target || benefitName == targetTitle));

                if (!matches)
                {
                    continue;
                }

                if (GetInt(drop, "requiredMinutesWatched") <= 0)
                {
                    return new ScanMatch(null, true);
                }

                var built = BuildCampaign(campaign);
                if (built == null || built.TimeBasedDrops.Count == 0)
                {
                    return new ScanMatch(null, true);
                }

                return new ScanMatch(built, false);
            }
        }

        return new ScanMatch(null, false);
    }

    private static readonly Dictionary<string, string> SlugByName = new(StringComparer.OrdinalIgnoreCase);

    private static async Task HarvestCampaignsAsync(
        TwitchGqlRepository repository, JsonElement? campaignsElement)
    {
        if (campaignsElement is not { ValueKind: JsonValueKind.Array } campaigns)
        {
            return;
        }

        var builtCampaigns = new List<DropCampaign>();

        foreach (var raw in campaigns.EnumerateArray())
        {
            var built = BuildCampaign(raw);
            if (built is null || string.IsNullOrEmpty(built.Id) || built.Game?.Name is null)
            {
                continue;
            }

            built.BadgeScanDiscovered = true;
            built.ScanTier = ComputeScanTier(built);
            builtCampaigns.Add(built);
        }

        foreach (var built in builtCampaigns)
        {
            if (string.IsNullOrEmpty(built.Game!.Slug))
            {
                string slug;
                lock (SlugByName)
                {
                    SlugByName.TryGetValue(built.Game.Name!, out slug);
                }

                if (string.IsNullOrEmpty(slug))
                {
                    var slugGame = await repository.FetchGameSlugAsync(built.Game.Name!);
                    slug = slugGame?.Slug ?? "";
                    lock (SlugByName)
                    {
                        SlugByName[built.Game.Name!] = slug;
                    }
                }

                built.Game.Slug = slug;
            }

            if (string.IsNullOrEmpty(built.Game.Slug))
            {
                continue;
            }

            lock (CacheGate)
            {
                Harvested[built.Id!] = built;
            }
        }
    }

    private static int ComputeScanTier(DropCampaign campaign)
    {
        foreach (var drop in campaign.TimeBasedDrops)
        {
            foreach (var edge in drop.BenefitEdges ?? new List<DropBenefitEdge>())
            {
                if (edge.Benefit?.DistributionType == DistributionType.BADGE)
                {
                    return CampaignRewardPriority.BadgeTier;
                }
            }
        }

        foreach (var drop in campaign.TimeBasedDrops)
        {
            foreach (var edge in drop.BenefitEdges ?? new List<DropBenefitEdge>())
            {
                if (edge.Benefit?.DistributionType == DistributionType.EMOTE)
                {
                    return CampaignRewardPriority.EmoteTier;
                }
            }
        }

        foreach (var drop in campaign.TimeBasedDrops)
        {
            var dropName = Normalize(drop.Name ?? "");
            var benefitName = Normalize(drop.BenefitEdges?.FirstOrDefault()?.Benefit?.Name ?? "");

            if (dropName.Length > 0 && Cache.ContainsKey(dropName))
            {
                return CampaignRewardPriority.BadgeTier;
            }

            if (benefitName.Length > 0 && Cache.ContainsKey(benefitName))
            {
                return CampaignRewardPriority.BadgeTier;
            }
        }

        return CampaignRewardPriority.RegularTier;
    }

    private static DropCampaign? BuildCampaign(JsonElement campaign)
    {
        if (campaign.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Game? game = null;
        if (campaign.TryGetProperty("game", out var gameElement) && gameElement.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(gameElement, "name");
            game = new Game
            {
                Id = GetString(gameElement, "id"),
                Name = name,
                DisplayName = name
            };
        }

        var drops = new List<TimeBasedDrop>();
        if (campaign.TryGetProperty("timeBasedDrops", out var dropsElement)
            && dropsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var drop in dropsElement.EnumerateArray())
            {
                var minutes = GetInt(drop, "requiredMinutesWatched");
                if (minutes <= 0)
                {
                    continue;
                }

                var benefit = GetBenefit(drop);
                drops.Add(new TimeBasedDrop
                {
                    Id = GetString(drop, "id"),
                    Name = GetString(drop, "name"),
                    StartAt = GetDateTime(drop, "startAt"),
                    EndAt = GetDateTime(drop, "endAt"),
                    RequiredMinutesWatched = minutes,
                    RequiredSubs = GetInt(drop, "requiredSubs"),
                    BenefitEdges = benefit == null
                        ? new List<DropBenefitEdge>()
                        : new List<DropBenefitEdge>
                        {
                            new()
                            {
                                Benefit = benefit
                            }
                        }
                });
            }
        }

        if (drops.Count == 0)
        {
            return null;
        }

        return new DropCampaign
        {
            Id = GetString(campaign, "id"),
            Name = GetString(campaign, "name"),
            Status = GetString(campaign, "status"),
            StartAt = drops.Min(drop => drop.StartAt),
            EndAt = GetDateTime(campaign, "endAt") == default
                ? drops.Max(drop => drop.EndAt)
                : GetDateTime(campaign, "endAt"),
            Game = game,
            BadgeScanDiscovered = true,
            TimeBasedDrops = drops
        };
    }

    private static string? GetBenefitName(JsonElement drop)
    {
        var benefit = GetBenefit(drop);
        return benefit?.Name;
    }

    private static DropBenefit? GetBenefit(JsonElement drop)
    {
        if (!drop.TryGetProperty("benefitEdges", out var edges) || edges.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("benefit", out var benefitElement)
                || benefitElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

                return new DropBenefit
                {
                    Id = GetString(benefitElement, "id"),
                    Name = GetString(benefitElement, "name"),
                    ImageAssetURL = GetString(benefitElement, "imageAssetURL"),
                    DistributionType = Enum.TryParse<DistributionType>(
                        GetString(benefitElement, "distributionType"), out var parsedType)
                        ? parsedType
                        : null
                };
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static DateTime GetDateTime(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : default;
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

        var contentMatch = Regex.Match(
            description,
            @"(?:watch|watching)\s+\d+\s+(?:minutes?|hours?)\s+of (?:a |an |the |any )?(.+?)(?: content| stream| streams| category| during|!|\.)",
            RegexOptions.IgnoreCase);
        if (contentMatch.Success)
        {
            names.Add(contentMatch.Groups[1].Value.Trim());
        }

        var bareMatch = Regex.Match(
            description, @"(?:watching|supporting) (.+?)(?:[.!?]|$)", RegexOptions.IgnoreCase);
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
            AddNameVariants(queries, name);
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
            .Take(10)
            .ToList();
    }

    private static void AddNameVariants(List<string> queries, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var cleaned = Regex.Replace(name, @"^(?:the\s+)?(?:\d{4}\s+)?", "", RegexOptions.IgnoreCase).Trim();
        if (cleaned.Length < 3)
        {
            return;
        }

        queries.Add(cleaned);

        var colonIndex = cleaned.IndexOf(':');
        var baseName = colonIndex > 0 ? cleaned[..colonIndex].Trim() : cleaned;

        if (!string.Equals(baseName, cleaned, StringComparison.Ordinal) && baseName.Length >= 3)
        {
            queries.Add(baseName);
        }

        var words = baseName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var keep = words.Length - 1; keep >= 2; keep--)
        {
            queries.Add(string.Join(' ', words.Take(keep)));
        }
    }

    public static string Normalize(string value) =>
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
        ScanTier = campaign.ScanTier,
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
                        Game = edge.Benefit.Game,
                        DistributionType = edge.Benefit.DistributionType
                    }
            }).ToList()
        }).ToList()
    };
}
