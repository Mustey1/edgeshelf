using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EdgeShelf;

public sealed class ShelfItem
{
    private ImageSource? _preview;
    private bool _previewLoaded;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public bool IsInternal { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFolder => Directory.Exists(Path);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsImage => File.Exists(Path) && ShelfStore.IsImageFile(Path);
    [System.Text.Json.Serialization.JsonIgnore]
    public string Glyph => IsFolder ? "▰" : IsImage ? "▧" : "▤";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Detail
    {
        get
        {
            if (!File.Exists(Path) && !Directory.Exists(Path)) return "File not found";
            if (IsFolder) return "Folder";
            try
            {
                long size = new FileInfo(Path).Length;
                return size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024d:0.#} KB" : $"{size / (1024d * 1024):0.#} MB";
            }
            catch (IOException) { return "File"; }
            catch (UnauthorizedAccessException) { return "File"; }
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? Preview
    {
        get
        {
            if (_previewLoaded) return _preview;
            _previewLoaded = true;
            try
            {
                if (IsImage)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 192;
                    bitmap.UriSource = new Uri(Path);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _preview = bitmap;
                }
                else if (!IsFolder && File.Exists(Path))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Path);
                    if (icon is not null)
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        _preview = source;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
            }
            return _preview;
        }
    }
}

public sealed class ShelfStore
{
    private readonly string _root;
    private readonly string _itemsDirectory;
    private readonly string _metadataPath;
    public ObservableCollection<ShelfItem> Items { get; } = new();

    public ShelfStore(string? storageRoot = null)
    {
        _root = storageRoot ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeShelf");
        _itemsDirectory = System.IO.Path.Combine(_root, "shelf");
        _metadataPath = System.IO.Path.Combine(_root, "shelf.json");
        try
        {
            var saved = JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(_metadataPath));
            if (saved is null) return;
            foreach (var item in saved.Where(item => !string.IsNullOrWhiteSpace(item.Path)))
                Items.Add(item);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public int AddPaths(IEnumerable<string> paths)
    {
        var added = new List<ShelfItem>();
        foreach (var path in paths)
        {
            try
            {
                var fullPath = System.IO.Path.GetFullPath(path);
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) continue;
                if (Items.Any(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase))) continue;
                var item = new ShelfItem { Path = fullPath };
                Items.Insert(0, item);
                added.Add(item);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { }
        }
        if (added.Count > 0 && !Save())
        {
            foreach (var item in added) Items.Remove(item);
            return 0;
        }
        return added.Count;
    }

    public bool AddImage(BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(_itemsDirectory);
            var path = System.IO.Path.Combine(_itemsDirectory, $"{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var item = new ShelfItem { Path = path, IsInternal = true };
            Items.Insert(0, item);
            if (!Save())
            {
                Items.Remove(item);
                File.Delete(path);
                return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or System.Runtime.InteropServices.ExternalException) { return false; }
    }

    public bool AddImageFile(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return false;
        try
        {
            Directory.CreateDirectory(_itemsDirectory);
            var path = System.IO.Path.Combine(_itemsDirectory, $"{Guid.NewGuid():N}.png");
            File.Copy(sourcePath, path);
            var item = new ShelfItem { Path = path, IsInternal = true };
            Items.Insert(0, item);
            if (Save()) return true;
            Items.Remove(item);
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    public bool AddText(string text)
    {
        try
        {
            Directory.CreateDirectory(_itemsDirectory);
            var path = System.IO.Path.Combine(_itemsDirectory, $"clipboard-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, text);
            var item = new ShelfItem { Path = path, IsInternal = true };
            Items.Insert(0, item);
            if (Save()) return true;
            Items.Remove(item);
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    public void Remove(ShelfItem item)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return;
        Items.RemoveAt(index);
        if (!Save())
        {
            Items.Insert(index, item);
            return;
        }
        if (!item.IsInternal) return;
        try
        {
            var fullPath = System.IO.Path.GetFullPath(item.Path);
            var storageRoot = System.IO.Path.GetFullPath(_itemsDirectory) + System.IO.Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(_root);
            var temporaryPath = _metadataPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Items.ToList()));
            File.Move(temporaryPath, _metadataPath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsImageFile(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
