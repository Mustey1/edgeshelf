using EdgeShelf;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Size = System.Windows.Size;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var window = new MainWindow();
        try
        {
            var content = (FrameworkElement)window.Content;
            Check(window.Width == 32 && window.ShowInTaskbar == false && window.Topmost,
                "Collapsed shell properties");
            Check(Find<Border>(window, "Trigger").Width == 32,
                "Hover target is wider than its visible handle");
            var startupMatch = typeof(MainWindow).GetMethod("StartupCommandMatches",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Check((bool)startupMatch.Invoke(null, new object?[]
                { "\"C:\\Apps\\EdgeShelf.exe\"", "C:\\Apps\\EdgeShelf.exe" })!
                && !(bool)startupMatch.Invoke(null, new object?[]
                { "\"C:\\Old\\EdgeShelf.exe\"", "C:\\Apps\\EdgeShelf.exe" })!,
                "Windows startup registration follows the current executable path");

            Invoke(window, "Open");
            Check(Find<Grid>(window, "PanelSurface").RenderTransform is TranslateTransform,
                "Panel slides in from the left edge");
            Pump(250);
            Layout(content, 360, 640);
            Check(window.Width == 360 && window.Height == 640
                && Find<Grid>(window, "PanelSurface").Visibility == Visibility.Visible,
                "Panel opens at its complete size");
            Check(Find<Border>(window, "Panel").Margin.Top == 20
                && Find<Grid>(window, "PanelSurface").Children
                    .OfType<System.Windows.Shapes.Path>().Count() == 2,
                "Upper and lower inverse corners travel with the panel");
            foreach (var name in new[] { "ShelfButton", "ClipboardButton", "SnippetsButton", "CalendarButton" })
                Check(Find<Button>(window, name).ActualHeight == 44, $"{name} is visible in the navigation rail");
            foreach (var (name, paths) in new[]
            {
                ("ShelfButton", 4), ("ClipboardButton", 7),
                ("SnippetsButton", 2), ("CalendarButton", 5)
            })
            {
                var button = Find<Button>(window, name);
                Check(button.Content is Viewbox { Width: 16, Height: 16, Child: Canvas canvas }
                    && canvas.Width == 24
                    && canvas.Children.OfType<System.Windows.Shapes.Path>().Count() == paths
                    && canvas.Children.OfType<System.Windows.Shapes.Path>()
                        .All(path => path.Data is not null && Equals(path.Stroke, button.Foreground)),
                    $"{name} uses a project-owned vector icon and navigation color");
            }

            foreach (var section in new[] { "Shelf", "Clipboard", "Snippets", "Calendar" })
            {
                Invoke(window, "SelectSection", section, false);
                Check(Find<TextBlock>(window, "SectionTitle").Text == section, $"{section} navigation");
            }

            Invoke(window, "SelectSection", "Clipboard", false);
            Check(Find<Button>(window, "ClipboardClearButton") is { Content: "Clear", Visibility: Visibility.Visible },
                "Clipboard clear action sits in the header");
            Check(window.FindName("ClipboardSearch") is null, "Clipboard search field is removed");
            var dragDataMethod = typeof(MainWindow).GetMethod("CreateClipboardDragData",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var textDrag = (System.Windows.DataObject)dragDataMethod.Invoke(null,
                new object[] { new ClipboardEntry { Kind = ClipboardKind.Text, Text = "Drag me" } })!;
            Check(textDrag.GetDataPresent(System.Windows.DataFormats.UnicodeText)
                && (string)textDrag.GetData(System.Windows.DataFormats.UnicodeText)! == "Drag me",
                "Clipboard text can be dragged into another app");
            var filePath = typeof(Program).Assembly.Location;
            var fileDrag = (System.Windows.DataObject)dragDataMethod.Invoke(null,
                new object[] { new ClipboardEntry { Kind = ClipboardKind.Files, Paths = new[] { filePath } } })!;
            Check(fileDrag.GetDataPresent(System.Windows.DataFormats.FileDrop)
                && ((string[])fileDrag.GetData(System.Windows.DataFormats.FileDrop)!).Single() == filePath,
                "Clipboard files can be dragged into another app");
            var clipboard = Find<ListBox>(window, "ClipboardList");
            var entries = new[]
            {
                new ClipboardEntry { Kind = ClipboardKind.Image },
                new ClipboardEntry { Kind = ClipboardKind.Url, Text = "https://calendar.google.com/calendar/example" }
            }.Concat(Enumerable.Range(0, 18).Select(index => new ClipboardEntry
            {
                Kind = ClipboardKind.Text,
                Text = $"Clipboard item {index}"
            })).ToArray();
            clipboard.ItemsSource = entries.Take(2);
            Layout(content, 360, 640);
            var shortCard = clipboard.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Check(shortCard is not null, "Clipboard card renders without overflow");
            var widthWithoutOverflow = Descendants<Border>(shortCard!).Single(border => border.Height == 70).ActualWidth;
            clipboard.ItemsSource = entries;
            Layout(content, 360, 640);
            var imageCard = clipboard.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var linkCard = clipboard.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
            var textCard = clipboard.ItemContainerGenerator.ContainerFromIndex(2) as ListBoxItem;
            Check(imageCard is not null && linkCard is not null && textCard is not null,
                "Clipboard cards are rendered");
            Check(Descendants<Button>(linkCard!).Any(button =>
                button.ToolTip?.ToString() == "Delete from Clipboard"
                && button.Visibility == Visibility.Collapsed),
                "Clipboard card has a hover delete button");
            Check(Descendants<Viewbox>(linkCard!).Count(icon => icon.Visibility == Visibility.Visible
                && icon.Child is Canvas canvas && canvas.Children.Count == 2) == 1,
                "Links use the supplied chain icon");
            Check(Descendants<Viewbox>(textCard!).Count(icon => icon.Visibility == Visibility.Visible
                && icon.Child is Canvas canvas && canvas.Children.Count == 3) == 1,
                "Text uses the supplied typing icon");
            var imageWidth = Descendants<Border>(imageCard!).Single(border => border.Height == 70).ActualWidth;
            var linkWidth = Descendants<Border>(linkCard!).Single(border => border.Height == 70).ActualWidth;
            Check(imageWidth > 200 && Math.Abs(imageWidth - linkWidth) < 0.5
                && Math.Abs(imageWidth - widthWithoutOverflow) < 0.5,
                "Clipboard cards keep their width when a scrollbar appears");
            var scrollBar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(clipboard)
                .FirstOrDefault(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
            Check(scrollBar is not null && scrollBar.Visibility == Visibility.Visible
                && scrollBar.Width == 8, "Clipboard uses the slim vertical scrollbar");
            var track = scrollBar!.Template.FindName("PART_Track", scrollBar)
                as System.Windows.Controls.Primitives.Track;
            Check(track?.Thumb is not null
                && track.Orientation == System.Windows.Controls.Orientation.Vertical
                && Descendants<Border>(track.Thumb).Any(border => border.Width == 4
                    && border.CornerRadius.TopLeft == 3),
                "Scrollbar thumb has rounded corners");

            Invoke(window, "OpenSettings");
            Check(Find<Button>(window, "ClipboardClearButton").Visibility == Visibility.Collapsed,
                "Clipboard clear action is hidden in settings");
            Check(Find<ScrollViewer>(window, "SettingsView").Visibility == Visibility.Visible,
                "Settings page opens");
            Layout(content, 360, 640);
            Check(Descendants<System.Windows.Controls.Primitives.ScrollBar>(
                    Find<ScrollViewer>(window, "SettingsView"))
                .Any(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical
                    && bar.Visibility == Visibility.Visible && bar.Width == 8),
                "Settings uses the same slim scrollbar");

            Invoke(window, "SelectSection", "Calendar", false);
            Find<StackPanel>(window, "CalendarSetup").Visibility = Visibility.Collapsed;
            Find<Grid>(window, "CalendarConnected").Visibility = Visibility.Visible;
            var list = Find<ListBox>(window, "CalendarList");
            list.ItemsSource = new[]
            {
                new CalendarEvent
                {
                    Title = "Test meeting",
                    Start = DateTimeOffset.Now.AddMinutes(10),
                    End = DateTimeOffset.Now.AddMinutes(40),
                    MeetingUrl = "https://meet.google.com/abc-defg-hij",
                    Provider = "Google Meet"
                },
                new CalendarEvent
                {
                    Title = "No link",
                    Start = DateTimeOffset.Now.AddMinutes(50),
                    End = DateTimeOffset.Now.AddMinutes(80)
                }
            };
            Layout(content, 360, 640);
            var first = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            var second = list.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
            Check(first is not null && second is not null, "Calendar cards are rendered");
            Check(Descendants<Button>(first!).Any(button => button.Content?.ToString() == "Join"
                && button.Visibility == Visibility.Visible), "Join button appears for meeting link");
            Check(Descendants<Button>(second!).Any(button => button.Content?.ToString() == "Join"
                && button.Visibility == Visibility.Collapsed), "Join button is hidden without a link");

            Invoke(window, "ClosePanel", true);
            Pump(250);
            Check(window.Width == 32 && window.Height == 160
                && Find<Grid>(window, "PanelSurface").Visibility == Visibility.Collapsed,
                "Panel collapses at its complete size");
            for (var index = 0; index < 10; index++)
            {
                Invoke(window, "Open");
                Check(window.Width == 360 && window.Height == 640, "Rapid open keeps complete geometry");
                Invoke(window, "ClosePanel", true);
                Check(window.Width == 360 && window.Height == 640,
                    "Close keeps geometry until slide-out finishes");
                Invoke(window, "Open");
            }
            Invoke(window, "ClosePanel", true);
            Pump(260);
            Check(window.Width == 32 && window.Height == 160
                && Find<Grid>(window, "PanelSurface").Visibility == Visibility.Collapsed,
                "Rapid transitions end in a complete collapsed state");
            Console.WriteLine("WPF shell and calendar card smoke test passed.");
        }
        finally
        {
            typeof(MainWindow).GetField("_quitting", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, true);
            window.Close();
        }
    }

    private static void Invoke(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);

    private static T Find<T>(MainWindow window, string name) where T : FrameworkElement =>
        (T)window.FindName(name)!;

    private static void Layout(FrameworkElement content, double width, double height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }
}
