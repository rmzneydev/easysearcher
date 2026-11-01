using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EasySearcher.Core;
using Microsoft.Win32;

namespace EasySearcher.App;

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    FileIndex? _index;
    SearchScope? _scope;
    string? _appliedRoot;
    CancellationTokenSource? _searchCts;

    public MainWindow()
    {
        InitializeComponent();
        RootBox.Text = _settings.RootPath ?? "";
        SystemBox.IsChecked = _settings.IncludeSystem;
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

        await ApplyRootAsync();
    }

    /// <summary>Valida la carpeta de RootBox y la usa como raiz de las busquedas (vacio = todas las unidades).</summary>
    async Task ApplyRootAsync()
    {
        var index = _index;
        string root = RootBox.Text.Trim();
        if (index is null || root == _appliedRoot) return;

        if (root.Length == 0)
        {
            _scope = null;
        }
        else
        {
            try
            {
                _scope = await Task.Run(() => index.CreateScope(root));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                RootBox.BorderBrush = Brushes.IndianRed;
                ResultsList.ItemsSource = null;
                ResultText.Text = ex.Message;
                return;
            }
            RootBox.Text = _scope.RootPath;
        }

        RootBox.ClearValue(BorderBrushProperty);
        _appliedRoot = RootBox.Text;
        _settings.RootPath = _scope?.RootPath;
        _settings.Save();
        await SearchAsync();
    }

    async Task SetRootAsync(string path)
    {
        RootBox.Text = path;
        await ApplyRootAsync();
    }

    async Task SearchAsync()
    {
        var index = _index;
        if (index is null) return;

        _searchCts?.Cancel();
        string text = QueryBox.Text.Trim();
        string where = _scope?.RootPath ?? "todas las unidades";
        if (text.Length == 0)
        {
            ResultsList.ItemsSource = null;
            ResultText.Text = $"Buscando en {where}";
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        var options = new SearchOptions
        {
            Kind = (EntryKind)KindBox.SelectedIndex,
            Scope = _scope,
            IncludeSystem = SystemBox.IsChecked == true,
        };

        try
        {
            var sw = Stopwatch.StartNew();
            var results = await Task.Run(() => index.Search(text, options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            ResultsList.ItemsSource = results;
            ResultText.Text = $"{results.Count:N0} resultados en {where}  ·  {sw.Elapsed.TotalMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
        }
    }

    void RootBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RootPlaceholder.Visibility = RootBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async void RootBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await ApplyRootAsync();
                if (_appliedRoot == RootBox.Text) QueryBox.Focus();
                break;
            case Key.Escape:
                e.Handled = true;
                RootBox.Text = _appliedRoot ?? "";
                RootBox.ClearValue(BorderBrushProperty);
                QueryBox.Focus();
                break;
        }
    }

    async void RootBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => await ApplyRootAsync();

    async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Carpeta raiz de las busquedas" };
        if (Directory.Exists(RootBox.Text)) dialog.InitialDirectory = RootBox.Text;
        if (dialog.ShowDialog(this) == true)
            await SetRootAsync(dialog.FolderName);
    }

    async void ClearRoot_Click(object sender, RoutedEventArgs e) => await SetRootAsync("");

    async void SearchHereMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResult r)
            await SetRootAsync(r.IsDirectory ? r.FullPath : r.DirectoryPath);
    }

    async void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueryPlaceholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        await SearchAsync();
    }

    async void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await SearchAsync();

    async void SystemBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeSystem = SystemBox.IsChecked == true;
        _settings.Save();
        await SearchAsync();
    }

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
