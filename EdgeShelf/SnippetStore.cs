using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace EdgeShelf;

public sealed class Snippet : INotifyPropertyChanged
{
    private string _title = "";
    private string _value = "";
    private string _category = "";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get => _title; set { _title = value; Notify(); } }
    public string Value { get => _value; set { _value = value; Notify(); Notify(nameof(Preview)); Notify(nameof(Detail)); } }
    public string Category { get => _category; set { _category = value; Notify(); Notify(nameof(Detail)); } }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Preview => Value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    [System.Text.Json.Serialization.JsonIgnore]
    public string Detail => string.IsNullOrWhiteSpace(Category) ? Preview : Category + " · " + Preview;
    [System.Text.Json.Serialization.JsonIgnore]
    public string SearchText => Title + "\n" + Category + "\n" + Value;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SnippetStore
{
    private readonly string _root;
    private readonly string _metadataPath;
    public ObservableCollection<Snippet> Items { get; } = new();

    public SnippetStore(string? storageRoot = null)
    {
        _root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeShelf");
        _metadataPath = Path.Combine(_root, "snippets.json");
        try
        {
            var saved = JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(_metadataPath));
            if (saved is not null)
                foreach (var snippet in saved) Items.Add(snippet);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public Snippet? Add(string title, string value, string category)
    {
        if (!IsValid(title, value)) return null;
        var item = new Snippet
        {
            Title = title.Trim(),
            Value = value,
            Category = category.Trim()
        };
        Items.Insert(0, item);
        if (Save()) return item;
        Items.Remove(item);
        return null;
    }

    public bool Update(Snippet item, string title, string value, string category)
    {
        if (!Items.Contains(item) || !IsValid(title, value)) return false;
        var oldTitle = item.Title;
        var oldValue = item.Value;
        var oldCategory = item.Category;
        var oldTime = item.UpdatedUtc;
        item.Title = title.Trim();
        item.Value = value;
        item.Category = category.Trim();
        item.UpdatedUtc = DateTime.UtcNow;
        if (Save()) return true;
        item.Title = oldTitle;
        item.Value = oldValue;
        item.Category = oldCategory;
        item.UpdatedUtc = oldTime;
        return false;
    }

    public Snippet? Duplicate(Snippet item) => Items.Contains(item)
        ? Add(item.Title + " copy", item.Value, item.Category)
        : null;

    public bool Remove(Snippet item)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return false;
        Items.RemoveAt(index);
        if (Save()) return true;
        Items.Insert(index, item);
        return false;
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

    private static bool IsValid(string title, string value) =>
        !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(value);
}
