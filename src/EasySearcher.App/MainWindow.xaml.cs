using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasySearcher.Core;

namespace EasySearcher.App;

public partial class MainWindow : Window
{
    FileIndex? _index;
    CancellationTokenSource? _searchCts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await BuildIndexAsync();
    }

    async Task BuildIndexAsync()
    {
        var drives = FileIndex.GetNtfsDrives();
        StatusText.Text = $"Indexando {string.Join(", ", drives.Select(d => d + ":"))}...";

        var sw = Stopwatch.StartNew();
        var index = await Task.Run(() => FileIndex.Build(drives));
        _index = index;

        var status = $"{index.Count:N0} entradas en {string.Join(" ", index.Volumes.Select(v => v.Drive + ":"))} " +
                     $"({sw.Elapsed.TotalSeconds:N1} s)";
        if (index.Failures.Count > 0)
            status += "   |   No indexadas: " + string.Join("; ", index.Failures.Select(f => $"{f.Key}: {f.Value.Message}"));
        StatusText.Text = status;

        await SearchAsync();
    }

    async Task SearchAsync()
    {
        var index = _index;
        if (index is null) return;

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        string text = QueryBox.Text.Trim();
        var kind = (EntryKind)KindBox.SelectedIndex;

        try
        {
            var sw = Stopwatch.StartNew();
            var results = await Task.Run(() => index.Search(text, kind, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            ResultsList.ItemsSource = results;
            ResultText.Text = $"{results.Count:N0} resultados  ·  {sw.Elapsed.TotalMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
        }
    }

    async void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => await SearchAsync();

    async void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await SearchAsync();

    void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = 0;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when ResultsList.Items.Count > 0:
                var first = (SearchResult)ResultsList.Items[0];
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Shell.ShowInFolder(first.FullPath);
                else Shell.Open(first.FullPath);
                e.Handled = true;
                break;
            case Key.Escape:
                QueryBox.Clear();
                e.Handled = true;
                break;
        }
    }

    void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Enter when ctrl:
                ShowSelectedInFolder();
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.C when ctrl:
                CopyToClipboard(Selected.Select(r => r.FullPath));
                e.Handled = true;
                break;
        }
    }

    void ResultItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { Content: SearchResult result })
            Shell.Open(result.FullPath);
    }

    void OpenMenu_Click(object sender, RoutedEventArgs e) => OpenSelected();

    void ShowInFolderMenu_Click(object sender, RoutedEventArgs e) => ShowSelectedInFolder();

    void CopyPathMenu_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected.Select(r => r.FullPath));

    void CopyNameMenu_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected.Select(r => r.Name));

    IEnumerable<SearchResult> Selected => ResultsList.SelectedItems.Cast<SearchResult>();

    void OpenSelected()
    {
        // Limite para no abrir cientos de ventanas por accidente
        foreach (var r in Selected.Take(10))
            Shell.Open(r.FullPath);
    }

    void ShowSelectedInFolder()
    {
        if (ResultsList.SelectedItem is SearchResult r)
            Shell.ShowInFolder(r.FullPath);
    }

    static void CopyToClipboard(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // El portapapeles puede estar ocupado por otra aplicacion
        }
    }
}
