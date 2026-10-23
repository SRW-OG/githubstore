using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Discovery;

/// <summary>Parses published source fields; never substitutes total stars for growth or descriptions for reviews.</summary>
public static class DiscoveryFeedParser
{
    private const string RepositoryPattern = @"[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}";
    public static string SourceName(DiscoveryFeedKind kind) => kind switch
    {
        DiscoveryFeedKind.TrendshiftDaily => "Trendshift", DiscoveryFeedKind.StarHistoryWeekly => "Star History",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    public static Uri SourceUri(DiscoveryFeedKind kind) => kind switch
    {
        DiscoveryFeedKind.TrendshiftDaily => new("https://trendshift.io/"),
        DiscoveryFeedKind.StarHistoryWeekly => new("https://www.star-history.com/"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static DiscoveryFeedResult Parse(DiscoveryFeedKind kind, string payload, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 8 * 1024 * 1024) throw Invalid();
        DiscoveryFeedResult result;
        if (kind == DiscoveryFeedKind.StarHistoryWeekly)
            result = TryWeeklyJson(payload, fetchedAt) ?? ParseWeeklyMarkdown(payload, fetchedAt);
        else if (kind == DiscoveryFeedKind.TrendshiftDaily)
            result = ParseTrendshift(payload, fetchedAt);
        else throw new ArgumentOutOfRangeException(nameof(kind));
        Validate(result);
        return result;
    }

    public static void Validate(DiscoveryFeedResult feed)
    {
        if (!Enum.IsDefined(feed.Kind) || feed.SourceUri != SourceUri(feed.Kind)
            || feed.SourceName != SourceName(feed.Kind) && feed.SourceName != SourceName(feed.Kind) + " · r.jina.ai"
            || feed.Entries is null || feed.Entries.Count is < 1 or > 100
            || string.IsNullOrWhiteSpace(feed.PeriodLabel) || feed.PeriodLabel.Length > 200 || feed.PeriodStart.HasValue != feed.PeriodEnd.HasValue
            || feed.Kind == DiscoveryFeedKind.TrendshiftDaily && feed.PeriodStart is null) throw Invalid();
        if (feed.PeriodStart is { } start && feed.PeriodEnd is { } end &&
            (end < start || end.DayNumber - start.DayNumber > 7 || start.Year < 2008 || end.Year > 2100 ||
                feed.Kind == DiscoveryFeedKind.TrendshiftDaily && start != end)) throw Invalid();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranks = new HashSet<int>();
        foreach (var entry in feed.Entries)
        {
            if (entry is null || string.IsNullOrEmpty(entry.RepositoryFullName)
                || !Regex.IsMatch(entry.RepositoryFullName, "^" + RepositoryPattern + "$", RegexOptions.CultureInvariant)
                || entry.RepositoryFullName.Split('/')[1] is "." or ".."
                || entry.RepositoryUri is not { IsAbsoluteUri: true, Scheme: "https", Host: "github.com" }
                || entry.RepositoryUri.AbsolutePath.Trim('/') != entry.RepositoryFullName
                || !string.IsNullOrEmpty(entry.RepositoryUri.Query) || !string.IsNullOrEmpty(entry.RepositoryUri.Fragment) || !string.IsNullOrEmpty(entry.RepositoryUri.UserInfo)
                || entry.SourceUri is not { IsAbsoluteUri: true, Scheme: "https" }
                || !string.IsNullOrEmpty(entry.SourceUri.UserInfo) || !string.IsNullOrEmpty(entry.SourceUri.Fragment)
                || !SourceHost(entry.SourceUri.Host, feed.Kind) || entry.Rank is < 1 or > 100
                || !names.Add(entry.RepositoryFullName) || !ranks.Add(entry.Rank)
                || entry.TotalStars < 0 || entry.StarsGained < 0 || entry.TrendScore < 0
                || entry.Description is null || entry.Description.Length > 10000 || entry.SourceReview?.Length > 10000
                || entry.SourceReview is not null && (string.IsNullOrWhiteSpace(entry.ReviewAttribution) || !SafeMentionUri(entry.ReviewUri) || entry.ReviewPublishedAt is null)
                || feed.Kind == DiscoveryFeedKind.StarHistoryWeekly && (entry.StarsGained is null || !entry.SourceUri.AbsolutePath.Trim('/').Equals(entry.RepositoryFullName, StringComparison.OrdinalIgnoreCase))
                || feed.Kind == DiscoveryFeedKind.TrendshiftDaily && !Regex.IsMatch(entry.SourceUri.AbsolutePath, @"^/repositories/[1-9]\d*$", RegexOptions.CultureInvariant)) throw Invalid();
        }
    }

    private static bool SourceHost(string host, DiscoveryFeedKind kind) => kind == DiscoveryFeedKind.TrendshiftDaily
        ? host.Equals("trendshift.io", StringComparison.OrdinalIgnoreCase) || host.Equals("www.trendshift.io", StringComparison.OrdinalIgnoreCase)
        : host.Equals("star-history.com", StringComparison.OrdinalIgnoreCase) || host.Equals("www.star-history.com", StringComparison.OrdinalIgnoreCase);

    private static DiscoveryFeedResult ParseWeeklyMarkdown(string text, DateTimeOffset fetchedAt)
    {
        var heading = text.IndexOf("Weekly All-time Random Pyramid", StringComparison.Ordinal);
        if (heading < 0) throw Invalid();
        var period = Regex.Match(text[heading..], @"Updated\s+(?<start>[A-Za-z]{3}\s+\d{1,2},\s+\d{4})\s+[–—-]\s+(?<end>[A-Za-z]{3}\s+\d{1,2},\s+\d{4})", RegexOptions.CultureInvariant);
        if (!period.Success || !DateOnly.TryParse(period.Groups["start"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var start)
            || !DateOnly.TryParse(period.Groups["end"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var end)) throw Invalid();
        var section = text.Substring(heading, period.Index);
        var entries = new List<DiscoveryEntry>();
        foreach (var line in section.Split('\n'))
        {
            if (!Regex.IsMatch(line, @"^\s*\d+\.\s+", RegexOptions.CultureInvariant)) continue;
            var row = Regex.Match(line, @"^\s*(?<rank>\d+)\.\s+.*\]\((?<url>https://(?:www\.)?star-history\.com/(?<linked>" + RepositoryPattern + @"))\)(?<repo>" + RepositoryPattern + @")\s+\+(?<gained>[\d,]+)\s*$", RegexOptions.CultureInvariant);
            if (!row.Success || !row.Groups["linked"].Value.Equals(row.Groups["repo"].Value, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            var name = row.Groups["repo"].Value;
            entries.Add(new DiscoveryEntry
            {
                RepositoryFullName = name, RepositoryUri = new("https://github.com/" + name), SourceUri = new(row.Groups["url"].Value),
                Rank = PositiveInt(row.Groups["rank"].Value), StarsGained = Integer(row.Groups["gained"].Value),
            });
        }
        return new DiscoveryFeedResult
        {
            Kind = DiscoveryFeedKind.StarHistoryWeekly, SourceName = SourceName(DiscoveryFeedKind.StarHistoryWeekly), SourceUri = SourceUri(DiscoveryFeedKind.StarHistoryWeekly),
            Entries = entries.AsReadOnly(), PeriodLabel = period.Value, PeriodStart = start, PeriodEnd = end, FetchedAt = fetchedAt,
        };
    }

    private static DiscoveryFeedResult? TryWeeklyJson(string payload, DateTimeOffset fetchedAt)
    {
        var jsonCandidates = new List<string>();
        if (payload.TrimStart().StartsWith('{')) jsonCandidates.Add(payload);
        foreach (Match script in Regex.Matches(payload, @"(?is)<script\b[^>]*(?:type\s*=\s*[""']application/json[""']|id\s*=\s*[""']__NEXT_DATA__[""'])[^>]*>(?<json>.*?)</script>", RegexOptions.CultureInvariant))
            jsonCandidates.Add(WebUtility.HtmlDecode(script.Groups["json"].Value));
        foreach (var candidate in jsonCandidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var data = FindWeeklyData(document.RootElement, 0);
                if (data is null) continue;
                var root = data.Value;
                var updated = root.GetProperty("updated_at").GetString();
                if (string.IsNullOrWhiteSpace(updated) || updated.Length > 100) throw Invalid();
                var entries = new List<DiscoveryEntry>();
                foreach (var repo in root.GetProperty("repos").EnumerateArray())
                {
                    var name = repo.GetProperty("name").GetString() ?? throw Invalid();
                    var growth = repo.GetProperty("new_stars").GetInt64();
                    var total = repo.GetProperty("stars_total").GetInt64();
                    if (growth < 0 || total < growth) throw Invalid();
                    entries.Add(new DiscoveryEntry
                    {
                        RepositoryFullName = name, RepositoryUri = new("https://github.com/" + name),
                        SourceUri = new("https://www.star-history.com/" + name), Rank = entries.Count + 1,
                        StarsGained = growth, TotalStars = total,
                    });
                }
                return new DiscoveryFeedResult
                {
                    Kind = DiscoveryFeedKind.StarHistoryWeekly, SourceName = SourceName(DiscoveryFeedKind.StarHistoryWeekly), SourceUri = SourceUri(DiscoveryFeedKind.StarHistoryWeekly),
                    Entries = entries.AsReadOnly(), PeriodLabel = "Updated " + updated, FetchedAt = fetchedAt,
                };
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
            { /* Unrecognized embedded JSON is not treated as ranking data. */ }
        }
        return null;
    }

    private static JsonElement? FindWeeklyData(JsonElement element, int depth)
    {
        if (depth > 24) return null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("updated_at", out var updated) && updated.ValueKind == JsonValueKind.String
                && element.TryGetProperty("repos", out var repos) && repos.ValueKind == JsonValueKind.Array) return element;
            foreach (var property in element.EnumerateObject()) if (FindWeeklyData(property.Value, depth + 1) is { } found) return found;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (FindWeeklyData(child, depth + 1) is { } found) return found;
        return null;
    }

    private static DiscoveryFeedResult ParseTrendshift(string payload, DateTimeOffset fetchedAt)
    {
        var roots = new List<JsonElement>();
        try
        {
            if (payload.TrimStart().StartsWith('{'))
            {
                using var direct = JsonDocument.Parse(payload);
                roots.Add(direct.RootElement.Clone());
            }
            else
            {
                var flight = new System.Text.StringBuilder();
                foreach (Match script in Regex.Matches(payload, @"(?s)self\.__next_f\.push\((?<array>\[.*?\])\)\s*;?\s*</script>", RegexOptions.CultureInvariant))
                {
                    using var record = JsonDocument.Parse(script.Groups["array"].Value);
                    var array = record.RootElement;
                    if (array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 1 && array[0].GetInt32() == 1 && array[1].ValueKind == JsonValueKind.String)
                        flight.Append(array[1].GetString());
                }
                foreach (var line in flight.ToString().Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 1 || !Regex.IsMatch(line[..colon], @"^[0-9a-fA-F]+$", RegexOptions.CultureInvariant)) continue;
                    var value = line[(colon + 1)..];
                    if (!value.StartsWith('[') && !value.StartsWith('{')) continue;
                    try { using var record = JsonDocument.Parse(value); roots.Add(record.RootElement.Clone()); }
                    catch (JsonException) { /* Non-JSON Flight record types are not executable or ranking data. */ }
                }
            }
            JsonElement? data = null, mentions = null;
            foreach (var root in roots)
            {
                data ??= FindArray(root, "initialData", 0);
                mentions ??= FindArray(root, "initialMentions", 0);
            }
            if (data is null) throw Invalid();
            var entries = new List<DiscoveryEntry>();
            DateOnly? period = null;
            foreach (var row in data.Value.EnumerateArray())
            {
                var name = row.GetProperty("full_name").GetString() ?? throw Invalid();
                var rank = row.GetProperty("rank").GetInt32();
                var sourceId = row.GetProperty("repository_id").GetInt64();
                if (sourceId <= 0 || !DateTimeOffset.TryParse(row.GetProperty("date").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) throw Invalid();
                var day = DateOnly.FromDateTime(date.UtcDateTime);
                if (period is not null && period != day) throw Invalid();
                period = day;
                var entry = new DiscoveryEntry
                {
                    RepositoryFullName = name, RepositoryUri = new("https://github.com/" + name),
                    SourceUri = new("https://trendshift.io/repositories/" + sourceId.ToString(CultureInfo.InvariantCulture)),
                    Rank = rank, TrendScore = row.GetProperty("score").GetDecimal(),
                    Description = row.GetProperty("repository_description").GetString() ?? "",
                    Language = row.TryGetProperty("repository_language", out var language) ? language.GetString() : null,
                    TotalStars = row.GetProperty("repository_stars").GetInt64(),
                    StarsGained = row.TryGetProperty("repository_stars_gained", out var gained) ? gained.GetInt64() : null,
                };
                if (entry.TotalStars < entry.StarsGained) throw Invalid();
                entries.Add(AttachMention(entry, mentions));
            }
            return new DiscoveryFeedResult
            {
                Kind = DiscoveryFeedKind.TrendshiftDaily, SourceName = SourceName(DiscoveryFeedKind.TrendshiftDaily), SourceUri = SourceUri(DiscoveryFeedKind.TrendshiftDaily),
                Entries = entries.OrderBy(entry => entry.Rank).ToArray(), PeriodStart = period, PeriodEnd = period,
                PeriodLabel = period?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), FetchedAt = fetchedAt,
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    private static JsonElement? FindArray(JsonElement value, string name, int depth)
    {
        if (depth > 24) return null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array) return array;
            foreach (var property in value.EnumerateObject()) if (FindArray(property.Value, name, depth + 1) is { } found) return found;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (FindArray(item, name, depth + 1) is { } found) return found;
        return null;
    }

    private static DiscoveryEntry AttachMention(DiscoveryEntry entry, JsonElement? mentions)
    {
        if (mentions is null) return entry;
        foreach (var mention in mentions.Value.EnumerateArray())
        {
            try
            {
                if (!string.Equals(mention.GetProperty("repository_full_name").GetString(), entry.RepositoryFullName, StringComparison.OrdinalIgnoreCase)) continue;
                var content = mention.GetProperty("content").GetString();
                var platform = mention.GetProperty("platform").GetString();
                var author = mention.GetProperty("author_handle").GetString();
                if (platform != "x" || string.IsNullOrWhiteSpace(content) || content.Length > 10000 ||
                    !Regex.IsMatch(author ?? "", @"^[A-Za-z0-9_]{1,50}$", RegexOptions.CultureInvariant) ||
                    !Uri.TryCreate(mention.GetProperty("link").GetString(), UriKind.Absolute, out var uri) || !SafeMentionUri(uri) ||
                    !DateTimeOffset.TryParse(mention.GetProperty("posted_at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var posted)) continue;
                if (entry.ReviewPublishedAt >= posted) continue;
                entry = entry with { SourceReview = content, ReviewAttribution = "Trendshift 收录的 X 提及 · @" + author,
                    ReviewUri = uri, ReviewPublishedAt = posted };
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { /* Malformed unrelated mentions never become reviews. */ }
        }
        return entry;
    }
    private static bool SafeMentionUri(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https" }
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Host.Equals("x.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase))
        && Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_]+/status/[0-9]+$", RegexOptions.CultureInvariant);
    private static int PositiveInt(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : throw Invalid();
    private static long Integer(string value) => Regex.IsMatch(value, @"^(?:\d+|\d{1,3}(?:,\d{3})+)$", RegexOptions.CultureInvariant)
        && long.TryParse(value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : throw Invalid();
    private static DiscoveryFeedException Invalid() => new("discovery_invalid_response");
}
