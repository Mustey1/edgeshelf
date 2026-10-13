using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdgeShelf;

public sealed class CalendarEvent
{
    public string Title { get; init; } = "Untitled event";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string MeetingUrl { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ProviderLabel => Provider.Length > 0 ? Provider : "No join link";
    public bool CanJoin => MeetingUrl.Length > 0;
    public string TimeRange => $"{Start.ToLocalTime():HH:mm}–{End.ToLocalTime():HH:mm}";
    public string RelativeTime
    {
        get
        {
            var now = DateTimeOffset.Now;
            if (Start <= now && End > now) return "Now";
            var minutes = Math.Max(1, (int)Math.Ceiling((Start - now).TotalMinutes));
            return minutes < 60 ? $"Starts in {minutes} min" : $"Starts at {Start.ToLocalTime():HH:mm}";
        }
    }
}

public static class CalendarEventParser
{
    private static readonly Regex UrlPattern = new(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<CalendarEvent> ParsePage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<CalendarEvent>();
        var result = new List<CalendarEvent>();
        foreach (var item in items.EnumerateArray())
        {
            if (Read(item, "status") == "cancelled") continue;
            if (!TryDateTime(item, "start", out var start) || !TryDateTime(item, "end", out var end)) continue;
            var (url, provider) = FindMeeting(item);
            result.Add(new CalendarEvent
            {
                Title = Read(item, "summary") is { Length: > 0 } title ? title : "Untitled event",
                Start = start,
                End = end,
                MeetingUrl = url,
                Provider = provider
            });
        }
        return result;
    }

    public static string? NextPageToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Read(document.RootElement, "nextPageToken");
    }

    private static bool TryDateTime(JsonElement item, string property, out DateTimeOffset result)
    {
        result = default;
        return item.TryGetProperty(property, out var node)
            && node.TryGetProperty("dateTime", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out result);
    }

    private static (string, string) FindMeeting(JsonElement item)
    {
        if (item.TryGetProperty("conferenceData", out var conference)
            && conference.TryGetProperty("entryPoints", out var points)
            && points.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in points.EnumerateArray())
            {
                if (Read(point, "entryPointType") == "video"
                    && Recognize(Read(point, "uri"), out var url, out var provider))
                    return (url, provider);
            }
        }
        if (Recognize(Read(item, "hangoutLink"), out var hangout, out var hangoutProvider))
            return (hangout, hangoutProvider);
        return FindMeetingFromText(Read(item, "location"), Read(item, "description"));
    }

    public static (string Url, string Provider) FindMeetingFromText(params string?[] fields)
    {
        foreach (var field in fields)
        {
            var content = WebUtility.HtmlDecode(field ?? "");
            foreach (Match match in UrlPattern.Matches(content))
            {
                var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
                if (Recognize(candidate, out var url, out var provider)) return (url, provider);
            }
        }
        return ("", "");
    }

    private static bool Recognize(string? candidate, out string url, out string provider)
    {
        url = "";
        provider = "";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host.ToLowerInvariant();
        if (host == "meet.google.com") provider = "Google Meet";
        else if (host == "zoom.us" || host.EndsWith(".zoom.us", StringComparison.Ordinal)) provider = "Zoom";
        else if (host is "teams.microsoft.com" or "teams.live.com") provider = "Microsoft Teams";
        else return false;
        url = uri.AbsoluteUri;
        return true;
    }

    private static string? Read(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
