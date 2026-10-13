using EdgeShelf;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var root = Path.Combine(Path.GetTempPath(), "EdgeShelf-Smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var storage = Path.Combine(root, "storage");
    var original = Path.Combine(root, "original.txt");
    var folder = Path.Combine(root, "folder");
    File.WriteAllText(original, "Original user file");
    Directory.CreateDirectory(folder);

    var shelf = new ShelfStore(storage);
    Check(shelf.AddPaths(new[] { original, folder, original }) == 2, "Files and folders add once");
    shelf = new ShelfStore(storage);
    Check(shelf.Items.Count == 2, "File and folder references survive restart");
    var originalItem = shelf.Items.Single(item => item.Path == original);
    shelf.Remove(originalItem);
    Check(File.Exists(original), "Removing a reference keeps the original file");

    var pixels = new byte[] { 0, 0, 255, 255 };
    var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
    Check(shelf.AddImage(image), "Image is saved");
    shelf = new ShelfStore(storage);
    var imageItem = shelf.Items.Single(item => item.IsInternal);
    Check(File.Exists(imageItem.Path), "Image survives restart as a local file");
    shelf.Remove(imageItem);
    Check(!File.Exists(imageItem.Path), "Removing an internal image deletes its local file");
    Check(new ShelfStore(storage).Items.Count == 1, "Removal survives restart");

    var clipboardRoot = Path.Combine(root, "clipboard-store");
    var clipboard = new ClipboardStore(clipboardRoot) { HistoryLimit = 2 };
    Check(clipboard.AddText("hello"), "Text is added to history");
    Check(!clipboard.AddText("hello"), "Consecutive duplicate text is ignored");
    Check(clipboard.AddText("https://example.com/path"), "URL is added");
    Check(clipboard.Items[0].Kind == ClipboardKind.Url, "URL is recognized");
    Check(clipboard.TogglePin(clipboard.Items[1]), "Text can be pinned");
    Check(clipboard.AddFiles(new[] { original }), "File is added to history");
    Check(clipboard.AddText("new text"), "New text is added");
    Check(clipboard.Items.Count == 3 && clipboard.Items.Any(entry => entry.Text == "hello"),
        "Pinned entry survives history trimming");
    Check(clipboard.AddImage(image), "Image is added to history");
    clipboard = new ClipboardStore(clipboardRoot);
    var clipImage = clipboard.Items.Single(entry => entry.Kind == ClipboardKind.Image);
    Check(File.Exists(clipImage.ImagePath), "Clipboard image survives restart");
    Check(clipboard.Items.Any(entry => entry.Text == "hello" && entry.Pinned), "Pin survives restart");
    Check(clipboard.SetHistoryLimit(20), "History limit can be changed");
    for (var index = 0; index < 12; index++)
        Check(clipboard.AddText("extra " + index), "New entries use the new limit");
    Check(clipboard.SetHistoryLimit(10), "History limit can be reduced");
    Check(clipboard.Items.Count == 11 && clipboard.Items.Any(entry => entry.Text == "hello" && entry.Pinned),
        "Lowering history limit preserves pinned entries");
    Check(clipboard.Clear(), "History can be cleared");
    Check(!File.Exists(clipImage.ImagePath) && new ClipboardStore(clipboardRoot).Items.Count == 0,
        "Clearing history removes metadata and internal images");

    var snippetsRoot = Path.Combine(root, "snippets-store");
    var snippets = new SnippetStore(snippetsRoot);
    Check(snippets.Add("", "value", "") is null, "Empty snippet title is rejected");
    var savedSnippet = snippets.Add("Email", "hello@example.com", "Personal");
    Check(savedSnippet is not null, "Snippet can be created");
    snippets = new SnippetStore(snippetsRoot);
    var loadedSnippet = snippets.Items.Single();
    Check(loadedSnippet.SearchText.Contains("Personal"), "Category is searchable");
    Check(snippets.Update(loadedSnippet, "Work email", "line 1\nline 2", "Work"), "Snippet can be edited");
    var duplicate = snippets.Duplicate(loadedSnippet);
    Check(duplicate is not null && snippets.Items.Count == 2, "Snippet can be duplicated");
    Check(snippets.Remove(loadedSnippet), "Snippet can be deleted");
    snippets = new SnippetStore(snippetsRoot);
    Check(snippets.Items.Count == 1 && snippets.Items[0].Value == "line 1\nline 2",
        "Snippet changes survive restart");

    const string calendarPage = """
    {
      "nextPageToken": "next-page",
      "items": [
        {"summary":"Meet sync","start":{"dateTime":"2026-09-17T15:00:00+03:00"},"end":{"dateTime":"2026-09-17T15:30:00+03:00"},
         "conferenceData":{"entryPoints":[{"entryPointType":"video","uri":"https://meet.google.com/abc-defg-hij"}]}},
        {"summary":"Zoom call","start":{"dateTime":"2026-09-17T16:00:00+03:00"},"end":{"dateTime":"2026-09-17T16:30:00+03:00"},
         "description":"Join at https://us02web.zoom.us/j/123?x=1&amp;y=2"},
        {"summary":"Teams call","start":{"dateTime":"2026-09-17T17:00:00+03:00"},"end":{"dateTime":"2026-09-17T17:30:00+03:00"},
         "location":"https://teams.microsoft.com/l/meetup-join/abc"},
        {"status":"cancelled","start":{"dateTime":"2026-09-17T18:00:00+03:00"},"end":{"dateTime":"2026-09-17T18:30:00+03:00"}},
        {"summary":"All day","start":{"date":"2026-09-17"},"end":{"date":"2026-09-18"}}
      ]
    }
    """;
    var meetings = CalendarEventParser.ParsePage(calendarPage);
    Check(meetings.Count == 3, "Timed active events are parsed");
    Check(meetings[0].Provider == "Google Meet" && meetings[1].Provider == "Zoom"
        && meetings[2].Provider == "Microsoft Teams", "Meeting links are recognized");
    Check(meetings[1].MeetingUrl.Contains("&y=2"), "HTML-encoded meeting URLs are decoded");
    Check(CalendarEventParser.NextPageToken(calendarPage) == "next-page", "Calendar pagination is parsed");
    var day = DateTime.Today;
    var ics = $"""
    BEGIN:VCALENDAR
    VERSION:2.0
    PRODID:-//EdgeShelf test//EN
    BEGIN:VEVENT
    UID:recurring@example.com
    DTSTART:{day.AddDays(-1):yyyyMMdd}T120000Z
    DTEND:{day.AddDays(-1):yyyyMMdd}T123000Z
    RRULE:FREQ=DAILY;COUNT=3
    SUMMARY:Daily sync
    DESCRIPTION:Meet at https://meet.google.com/abc-defg-hij
    END:VEVENT
    BEGIN:VEVENT
    UID:all-day@example.com
    DTSTART;VALUE=DATE:{day:yyyyMMdd}
    DTEND;VALUE=DATE:{day.AddDays(1):yyyyMMdd}
    SUMMARY:All day
    END:VEVENT
    END:VCALENDAR
    """;
    var icalEvents = IcalCalendarClient.ParseToday(ics, new DateTimeOffset(day.AddHours(10)));
    Check(icalEvents.Count == 1 && icalEvents[0].Title == "Daily sync",
        "Recurring iCal event appears today and all-day event is skipped");
    Check(icalEvents[0].Provider == "Google Meet", "iCal meeting link is recognized");
    var icalRoot = Path.Combine(root, "ical-store");
    Directory.CreateDirectory(icalRoot);
    File.WriteAllBytes(Path.Combine(icalRoot, "calendar-ical.bin"), ProtectedData.Protect(
        Encoding.UTF8.GetBytes("https://calendar.google.com/calendar/ical/test/private-test/basic.ics"),
        null, DataProtectionScope.CurrentUser));
    var icalClient = new IcalCalendarClient(icalRoot);
    Check(icalClient.IsConnected, "Encrypted private calendar link loads after restart");
    icalClient.Disconnect();
    Check(!new IcalCalendarClient(icalRoot).IsConnected, "Disconnect removes private calendar link");
    var calendarRoot = Path.Combine(root, "calendar-store");
    Directory.CreateDirectory(calendarRoot);
    File.WriteAllText(Path.Combine(calendarRoot, "calendar-client.json"),
        "{\"ClientId\":\"test.apps.googleusercontent.com\"}");
    var fakeToken = Encoding.UTF8.GetBytes("{\"AccessToken\":\"test-access\",\"RefreshToken\":\"test-refresh\",\"ExpiresUtc\":\"2030-01-01T00:00:00+00:00\"}");
    File.WriteAllBytes(Path.Combine(calendarRoot, "calendar-token.bin"),
        ProtectedData.Protect(fakeToken, null, DataProtectionScope.CurrentUser));
    var calendar = new GoogleCalendarClient(calendarRoot);
    Check(calendar.IsConnected, "Windows-protected calendar token can be loaded");
    calendar.Disconnect();
    Check(!new GoogleCalendarClient(calendarRoot).IsConnected, "Disconnect removes the local token");
    Console.WriteLine("Shelf, clipboard, snippets, and calendar smoke tests passed.");
}
finally
{
    var resolved = Path.GetFullPath(root);
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolved).StartsWith("EdgeShelf-Smoke-", StringComparison.Ordinal))
        Directory.Delete(resolved, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
