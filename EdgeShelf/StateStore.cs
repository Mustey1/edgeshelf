using System.IO;
using System.Text.Json;

namespace EdgeShelf;

internal sealed class AppState
{
    public string LastSection { get; set; } = "Shelf";
    public bool StartWithWindows { get; set; }
    public string Shortcut { get; set; } = "Ctrl+Shift+Space";
    public int HoverDelayMs { get; set; } = 200;
    public int CloseDelayMs { get; set; } = 550;
    public bool AutoAddCopiedImages { get; set; } = true;
    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardHistoryLimit { get; set; } = 100;
}

internal static class StateStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeShelf");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "state.json");

    public static AppState Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new AppState();
        }
        catch (IOException) { return new AppState(); }
        catch (UnauthorizedAccessException) { return new AppState(); }
        catch (JsonException) { return new AppState(); }
    }

    public static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, FilePath, true);
        }
        catch (IOException) { /* Keep the current session usable if storage is unavailable. */ }
        catch (UnauthorizedAccessException) { }
    }
}
