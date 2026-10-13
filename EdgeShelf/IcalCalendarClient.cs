using Ical.Net;
using Ical.Net.DataTypes;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace EdgeShelf;

public sealed class IcalCalendarClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _path;
    private string _url = "";

    public bool IsConnected => _url.Length > 0;

    public IcalCalendarClient(string? storageRoot = null)
    {
        var root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeShelf");
        _path = Path.Combine(root, "calendar-ical.bin");
        try
        {
            _url = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser));
            ValidateUrl(_url);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException) { _url = ""; }
    }

    public async Task ConnectAsync(string url, CancellationToken cancellationToken = default)
    {
        url = url.Trim();
        ValidateUrl(url);
        await FetchEventsAsync(url, cancellationToken);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(url), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path + ".tmp", bytes);
        File.Move(_path + ".tmp", _path, true);
        _url = url;
    }

    public Task<IReadOnlyList<CalendarEvent>> GetTodayEventsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect a calendar first.");
        return FetchEventsAsync(_url, cancellationToken);
    }

    public void Disconnect()
    {
        _url = "";
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "calendar.google.com"
            || !uri.AbsolutePath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Paste the Secret address in iCal format from Google Calendar.");
    }

    private static async Task<IReadOnlyList<CalendarEvent>> FetchEventsAsync(
        string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Calendar feed returned {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is > 10_000_000)
            throw new InvalidDataException("Calendar feed is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > 10_000_000)
                throw new InvalidDataException("Calendar feed is too large.");
            buffer.Write(chunk, 0, count);
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return ParseToday(text);
    }

    public static IReadOnlyList<CalendarEvent> ParseToday(string ics, DateTimeOffset? nowOverride = null)
    {
        var calendar = Calendar.Load(ics) ?? throw new InvalidDataException("Invalid calendar feed.");
        var now = nowOverride ?? DateTimeOffset.Now;
        var today = now.ToLocalTime().Date;
        var tomorrow = new DateTimeOffset(today.AddDays(1));
        var startSearch = new CalDateTime(today.AddDays(-1));
        var result = new List<CalendarEvent>();
        foreach (var occurrence in calendar.GetOccurrences(startSearch)
            .TakeWhile(item => item.Period.StartTime.AsUtc < tomorrow.UtcDateTime))
        {
            if (occurrence.Source is not Ical.Net.CalendarComponents.CalendarEvent source
                || !source.IsActive || !occurrence.Period.StartTime.HasTime) continue;
            var endTime = occurrence.Period.EffectiveEndTime;
            if (endTime is null) continue;
            var start = new DateTimeOffset(occurrence.Period.StartTime.AsUtc, TimeSpan.Zero);
            var end = new DateTimeOffset(endTime.AsUtc, TimeSpan.Zero);
            if (end <= now || start >= tomorrow) continue;
            var (meetingUrl, provider) = CalendarEventParser.FindMeetingFromText(
                source.Location, source.Description);
            result.Add(new CalendarEvent
            {
                Title = string.IsNullOrWhiteSpace(source.Summary) ? "Untitled event" : source.Summary,
                Start = start,
                End = end,
                MeetingUrl = meetingUrl,
                Provider = provider
            });
            if (result.Count >= 100) break;
        }
        return result.OrderBy(item => item.Start).Take(8).ToArray();
    }
}
