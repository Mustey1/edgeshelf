using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdgeShelf;

public sealed class GoogleCalendarClient
{
    private const string Scope = "https://www.googleapis.com/auth/calendar.events.readonly";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _root;
    private readonly string _clientPath;
    private readonly string _tokenPath;
    private GoogleToken? _token;

    public string ClientId { get; private set; } = "";
    public bool IsConnected => ClientId.Length > 0 && _token?.RefreshToken.Length > 0;

    public GoogleCalendarClient(string? storageRoot = null)
    {
        _root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeShelf");
        _clientPath = Path.Combine(_root, "calendar-client.json");
        _tokenPath = Path.Combine(_root, "calendar-token.bin");
        try
        {
            ClientId = JsonSerializer.Deserialize<CalendarClientSettings>(File.ReadAllText(_clientPath))?.ClientId ?? "";
            var protectedBytes = File.ReadAllBytes(_tokenPath);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            _token = JsonSerializer.Deserialize<GoogleToken>(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or CryptographicException) { }
    }

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken = default)
    {
        clientId = clientId.Trim();
        if (!clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            throw new ArgumentException("Enter a Desktop app OAuth Client ID.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/";
            var authorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + FormQuery(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = Scope,
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            });
            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            string code;
            while (true)
            {
                using var browser = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = browser.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(timeout.Token);
                var requestPath = requestLine?.Split(' ', 3).ElementAtOrDefault(1);
                var query = ParseQuery(requestPath);
                if (!query.TryGetValue("state", out var returnedState) || returnedState != state)
                {
                    await WriteBrowserResponse(stream, "Invalid authorization response.", timeout.Token);
                    continue;
                }
                if (query.TryGetValue("error", out var error))
                {
                    await WriteBrowserResponse(stream, "Calendar connection was not completed.", timeout.Token);
                    throw new InvalidOperationException($"Google authorization: {error}");
                }
                if (!query.TryGetValue("code", out code!) || string.IsNullOrWhiteSpace(code))
                {
                    await WriteBrowserResponse(stream, "Authorization code was missing.", timeout.Token);
                    throw new InvalidOperationException("Google did not return an authorization code.");
                }
                await WriteBrowserResponse(stream, "Calendar connected. You can close this tab.", timeout.Token);
                break;
            }

            var response = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code"
            }, cancellationToken);
            var token = ParseToken(response, "");
            if (token.RefreshToken.Length == 0)
                throw new InvalidOperationException("Google did not issue a refresh token. Reconnect and grant access.");
            SaveConnection(clientId, token);
        }
        finally { listener.Stop(); }
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetTodayEventsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect Google Calendar first.");
        var token = await AccessTokenAsync(cancellationToken);
        var today = new DateTimeOffset(DateTime.Today);
        var tomorrow = new DateTimeOffset(DateTime.Today.AddDays(1));
        var events = new List<CalendarEvent>();
        string? pageToken = null;
        for (var page = 0; page < 10; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["timeMin"] = today.ToString("o"),
                ["timeMax"] = tomorrow.ToString("o"),
                ["singleEvents"] = "true",
                ["orderBy"] = "startTime",
                ["showDeleted"] = "false",
                ["maxResults"] = "100"
            };
            if (pageToken is not null) query["pageToken"] = pageToken;
            var url = "https://www.googleapis.com/calendar/v3/calendars/primary/events?" + FormQuery(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google Calendar returned {(int)response.StatusCode}.");
            events.AddRange(CalendarEventParser.ParsePage(json));
            pageToken = CalendarEventParser.NextPageToken(json);
            if (pageToken is null) break;
        }
        var now = DateTimeOffset.Now;
        return events.Where(item => item.End > now && item.Start < tomorrow)
            .OrderBy(item => item.Start).Take(8).ToArray();
    }

    public void Disconnect()
    {
        _token = null;
        try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is null || _token.RefreshToken.Length == 0)
            throw new InvalidOperationException("Connect Google Calendar first.");
        if (_token.AccessToken.Length > 0 && _token.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            return _token.AccessToken;
        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["refresh_token"] = _token.RefreshToken,
            ["grant_type"] = "refresh_token"
        }, cancellationToken);
        var updated = ParseToken(response, _token.RefreshToken);
        SaveConnection(ClientId, updated);
        return updated.AccessToken;
    }

    private void SaveConnection(string clientId, GoogleToken token)
    {
        Directory.CreateDirectory(_root);
        var clientBytes = JsonSerializer.SerializeToUtf8Bytes(new CalendarClientSettings { ClientId = clientId });
        var tokenBytes = JsonSerializer.SerializeToUtf8Bytes(token);
        var protectedBytes = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_clientPath + ".tmp", clientBytes);
        File.Move(_clientPath + ".tmp", _clientPath, true);
        File.WriteAllBytes(_tokenPath + ".tmp", protectedBytes);
        File.Move(_tokenPath + ".tmp", _tokenPath, true);
        ClientId = clientId;
        _token = token;
    }

    private static async Task<string> PostTokenAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var body = new FormUrlEncodedContent(values);
        using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", body, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google authorization returned {(int)response.StatusCode}.");
        return json;
    }

    private static GoogleToken ParseToken(string json, string existingRefreshToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var value)
            ? value.GetString() ?? existingRefreshToken : existingRefreshToken;
        var seconds = root.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600;
        return new GoogleToken
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds - 60))
        };
    }

    private static string FormQuery(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is null) return result;
        var question = path.IndexOf('?');
        if (question < 0) return result;
        foreach (var part in path[(question + 1)..].Split('&'))
        {
            var split = part.IndexOf('=');
            if (split < 0) continue;
            result[WebUtility.UrlDecode(part[..split])] = WebUtility.UrlDecode(part[(split + 1)..]);
        }
        return result;
    }

    private static async Task WriteBrowserResponse(NetworkStream stream, string message, CancellationToken cancellationToken)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Edge Shelf</title>" +
            "<p style=\"font:16px Segoe UI,sans-serif;padding:24px\">" + WebUtility.HtmlEncode(message) + "</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private sealed class CalendarClientSettings { public string ClientId { get; set; } = ""; }
    private sealed class GoogleToken
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTimeOffset ExpiresUtc { get; set; }
    }
}
