using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EdgeShelf;

public enum ClipboardKind { Text, Url, Image, Files }

public sealed class ClipboardEntry : INotifyPropertyChanged
{
    private bool _pinned;
    private ImageSource? _preview;
    private bool _previewLoaded;

    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipboardKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string[] Paths { get; set; } = Array.Empty<string>();
    public string ImagePath { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool Pinned
    {
        get => _pinned;
        set
        {
            if (_pinned == value) return;
            _pinned = value;
            Notify();
            Notify(nameof(PinLabel));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Title => Kind switch
    {
        ClipboardKind.Url => Uri.TryCreate(Text, UriKind.Absolute, out var uri) ? uri.Host : "Link",
        ClipboardKind.Image => "Image",
        ClipboardKind.Files => Paths.Length == 1 ? Path.GetFileName(Paths[0]) : $"{Paths.Length} files",
        _ => string.IsNullOrWhiteSpace(Text) ? "Text" : Text.Replace('\r', ' ').Replace('\n', ' ').Trim()
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string Detail => Kind switch
    {
        ClipboardKind.Url => Text,
        ClipboardKind.Text => CreatedUtc.ToLocalTime().ToString("g"),
        ClipboardKind.Image => CreatedUtc.ToLocalTime().ToString("g"),
        _ => Paths.Length == 1 ? "File" : string.Join(", ", Paths.Take(3).Select(Path.GetFileName))
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string Glyph => Kind switch
    {
        ClipboardKind.Url => "↗",
        ClipboardKind.Image => "▧",
        ClipboardKind.Files => "▤",
        _ => "≡"
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string PinLabel => Pinned ? "Unpin" : "Pin permanently";

    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? Preview
    {
        get
        {
            if (_previewLoaded) return _preview;
            _previewLoaded = true;
            if (Kind != ClipboardKind.Image || !File.Exists(ImagePath)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
                bitmap.UriSource = new Uri(ImagePath);
                bitmap.EndInit();
                bitmap.Freeze();
                _preview = bitmap;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException) { }
            return _preview;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string SearchText => Kind == ClipboardKind.Files
        ? string.Join(" ", Paths.Select(Path.GetFileName))
        : Text;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ClipboardStore
{
    private readonly string _root;
    private readonly string _imagesDirectory;
    private readonly string _metadataPath;
    public int HistoryLimit { get; set; } = 100;
    public ObservableCollection<ClipboardEntry> Items { get; } = new();

    public ClipboardStore(string? storageRoot = null)
    {
        _root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeShelf");
        _imagesDirectory = Path.Combine(_root, "clipboard");
        _metadataPath = Path.Combine(_root, "clipboard.json");
        try
        {
            var saved = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(_metadataPath));
            if (saved is not null)
                foreach (var entry in saved) Items.Add(entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public bool AddText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var kind = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" ? ClipboardKind.Url : ClipboardKind.Text;
        return Add(new ClipboardEntry
        {
            Kind = kind,
            Text = text,
            Fingerprint = Hash("text\0" + text)
        });
    }

    public bool AddFiles(IEnumerable<string> paths)
    {
        var valid = paths.Select(path =>
        {
            try { return Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return ""; }
        }).Where(path => path.Length > 0).ToArray();
        if (valid.Length == 0) return false;
        return Add(new ClipboardEntry
        {
            Kind = ClipboardKind.Files,
            Paths = valid,
            Fingerprint = Hash("files\0" + string.Join("\0", valid.Select(path => path.ToUpperInvariant())))
        });
    }

    public bool AddImage(BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bytes = stream.ToArray();
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            if (Items.FirstOrDefault()?.Fingerprint == fingerprint) return false;
            Directory.CreateDirectory(_imagesDirectory);
            var path = Path.Combine(_imagesDirectory, $"{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, bytes);
            var entry = new ClipboardEntry
            {
                Kind = ClipboardKind.Image,
                ImagePath = path,
                Fingerprint = fingerprint
            };
            if (Add(entry)) return true;
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Runtime.InteropServices.ExternalException) { }
        return false;
    }

    private bool Add(ClipboardEntry entry)
    {
        if (Items.FirstOrDefault()?.Fingerprint == entry.Fingerprint) return false;
        var previous = Items.ToList();
        Items.Insert(0, entry);
        var trimmed = Trim();
        if (!Save())
        {
            Restore(previous);
            return false;
        }
        foreach (var old in trimmed) DeleteImage(old);
        return true;
    }

    private List<ClipboardEntry> Trim()
    {
        var removed = new List<ClipboardEntry>();
        var unpinned = Items.Where(entry => !entry.Pinned).ToList();
        foreach (var entry in unpinned.Skip(Math.Max(0, HistoryLimit)))
        {
            Items.Remove(entry);
            removed.Add(entry);
        }
        return removed;
    }

    public bool TogglePin(ClipboardEntry entry)
    {
        if (!Items.Contains(entry)) return false;
        entry.Pinned = !entry.Pinned;
        var previous = Items.ToList();
        var trimmed = Trim();
        if (!Save())
        {
            Restore(previous);
            entry.Pinned = !entry.Pinned;
            return false;
        }
        foreach (var old in trimmed) DeleteImage(old);
        return true;
    }

    public bool Remove(ClipboardEntry entry)
    {
        var index = Items.IndexOf(entry);
        if (index < 0) return false;
        Items.RemoveAt(index);
        if (!Save())
        {
            Items.Insert(index, entry);
            return false;
        }
        DeleteImage(entry);
        return true;
    }

    public bool Clear()
    {
        var previous = Items.ToList();
        Items.Clear();
        if (!Save())
        {
            Restore(previous);
            return false;
        }
        foreach (var entry in previous) DeleteImage(entry);
        return true;
    }

    public bool SetHistoryLimit(int limit)
    {
        if (limit is < 10 or > 500) return false;
        var previous = Items.ToList();
        var oldLimit = HistoryLimit;
        HistoryLimit = limit;
        var trimmed = Trim();
        if (!Save())
        {
            HistoryLimit = oldLimit;
            Restore(previous);
            return false;
        }
        foreach (var entry in trimmed) DeleteImage(entry);
        return true;
    }

    private void Restore(List<ClipboardEntry> previous)
    {
        Items.Clear();
        foreach (var entry in previous) Items.Add(entry);
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(_root);
            var temporary = _metadataPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Items.ToList()));
            File.Move(temporary, _metadataPath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private void DeleteImage(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardKind.Image) return;
        try
        {
            var path = Path.GetFullPath(entry.ImagePath);
            var root = Path.GetFullPath(_imagesDirectory) + Path.DirectorySeparatorChar;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
