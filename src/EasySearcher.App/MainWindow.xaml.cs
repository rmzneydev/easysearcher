using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasySearcher.Core;
using EasySearcher.Core.Service;
using Microsoft.Win32;

namespace EasySearcher.App;

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly Dictionary<GridViewColumn, (SortColumn Sort, string Title)> _columns;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    ISearchService? _service;
    bool _usingLocalEngine;
    EngineStatus? _status;
    bool _polling;
    string? _appliedRoot;
    bool _rootApplied;
    int _searchVersion;

    public MainWindow()
    {
        InitializeComponent();
        RootBox.Text = _settings.RootPath ?? "";
        SystemBox.IsChecked = _settings.IncludeSystem;

        _columns = new()
        {
            [NameColumn] = (SortColumn.Name, "Nombre"),
            [FolderColumn] = (SortColumn.Folder, "Carpeta"),
            [SizeColumn] = (SortColumn.Size, "Tamaño"),
            [ModifiedColumn] = (SortColumn.Modified, "Modificado"),
        };
        UpdateSortHeaders();

        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        Loaded += async (_, _) =>
        {
            await PollStatusAsync();
            _statusTimer.Start();
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _service?.Dispose();
        };
    }

    /// <summary>
    /// Se conecta al servicio si hace falta, actualiza la barra de estado y repite la busqueda si el
    /// indice cambio. Se llama cada segundo.
    /// </summary>
    async Task PollStatusAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            _service ??= await ConnectAsync();
            if (_service is not { } service) return;

            EngineStatus status;
            try
            {
                status = await Task.Run(service.GetStatus);
            }
            catch (SearchServiceException)
            {
                // El servicio se detuvo: se reintenta en la siguiente vuelta
                service.Dispose();
                _service = null;
                _status = null;
                StatusText.Text = "Se perdio la conexion con el servicio de EasySearcher. Reintentando...";
                return;
            }

            var previous = _status;
            _status = status;
            UpdateStatus(status);

            if (!status.Ready) return;
            if (!_rootApplied)
                await ApplyRootAsync();
            else if (previous is { Ready: true } && previous.ChangeCounter != status.ChangeCounter)
                await SearchAsync(preserveView: true);
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// Usa el servicio de Windows. Si no esta y la app corre como administrador, construye el indice en
    /// este mismo proceso (util para desarrollo).
    /// </summary>
    async Task<ISearchService?> ConnectAsync()
    {
        try
        {
            return await Task.Run(PipeSearchClient.Connect);
        }
        catch (SearchServiceException)
        {
        }

        if (IsElevated())
        {
            _usingLocalEngine = true;
            return new SearchEngine(FileIndex.GetNtfsDrives());
        }

        StatusText.Text = "El servicio de EasySearcher no esta en ejecucion. Reintentando...";
        return null;
    }

    static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    void UpdateStatus(EngineStatus status)
    {
        string source = _usingLocalEngine ? "  ·  sin servicio (indice en esta ventana)" : "";
        if (!status.Ready)
        {
            StatusText.Text = status.Failures.Length > 0
                ? string.Join("; ", status.Failures)
                : $"Indexando unidades NTFS... ({status.BuildSeconds:N0} s){source}";
            return;
        }

        var text = $"{status.Entries:N0} entradas en {string.Join(" ", status.Drives.Select(d => d + ":"))}";
        text += status.NotLive.Length == 0
            ? "  ·  actualizado en vivo"
            : $"  ·  sin actualizacion en vivo en {string.Join(" ", status.NotLive.Select(d => d + ":"))}";
        if (status.Failures.Length > 0)
            text += "   |   No indexadas: " + string.Join("; ", status.Failures);
        StatusText.Text = text + source;
    }

    bool IsReady => _service is not null && _status is { Ready: true };

    /// <summary>Valida la carpeta de RootBox y la usa como raiz de las busquedas (vacio = todas las unidades).</summary>
    async Task ApplyRootAsync()
    {
        if (!IsReady || _service is not { } service) return;
        string root = RootBox.Text.Trim();
        if (_rootApplied && root == (_appliedRoot ?? "")) return;

        string? resolved = null;
        if (root.Length > 0)
        {
            try
            {
                resolved = await Task.Run(() => service.ResolveRoot(root));
            }
            catch (SearchServiceException ex)
            {
                RootBox.BorderBrush = Brushes.IndianRed;
                ResultsList.ItemsSource = null;
                ResultText.Text = ex.Message;
                return;
            }
            RootBox.Text = resolved;
        }

        RootBox.ClearValue(BorderBrushProperty);
        _appliedRoot = resolved;
        _rootApplied = true;
        _settings.RootPath = resolved;
        _settings.Save();
        await SearchAsync();
    }

    async Task SetRootAsync(string path)
    {
        RootBox.Text = path;
        await ApplyRootAsync();
    }

    /// <param name="preserveView">Al refrescar por cambios en disco: no tocar la lista si los resultados son
    /// los mismos y, si cambiaron, conservar la seleccion y la posicion del scroll.</param>
    async Task SearchAsync(bool preserveView = false)
    {
        if (!IsReady || !_rootApplied || _service is not { } service) return;

        int version = ++_searchVersion;
        string text = QueryBox.Text.Trim();
        string where = _appliedRoot ?? "todas las unidades";
        if (text.Length == 0)
        {
            ResultsList.ItemsSource = null;
            ResultText.Text = $"Buscando en {where}";
            return;
        }

        var current = ResultsList.ItemsSource as VirtualResultList;
        var request = new SearchRequest(
            text,
            (EntryKind)KindBox.SelectedIndex,
            _appliedRoot,
            SystemBox.IsChecked == true,
            _settings.SortColumn,
            _settings.SortDescending,
            preserveView ? current?.SearchId ?? 0 : 0);

        try
        {
            var response = await Task.Run(() => service.Search(request));
            // Si mientras tanto se lanzo otra busqueda, esta respuesta ya no interesa
            if (version != _searchVersion) return;

            ResultText.Text = $"{response.Total:N0} resultados en {where}  ·  {response.ElapsedMs:N0} ms";
            if (preserveView && response.Unchanged) return;

            var list = new VirtualResultList(service, response.SearchId, response.Total,
                onExpired: () => Dispatcher.InvokeAsync(() => SearchAsync(preserveView: true)));
            if (preserveView) ReplaceResultsKeepingView(list);
            else ResultsList.ItemsSource = list;
        }
        catch (SearchServiceException ex)
        {
            if (version == _searchVersion) ResultText.Text = ex.Message;
        }
    }

    async void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        // El encabezado de relleno a la derecha de la ultima columna no tiene Column
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || !_columns.TryGetValue(column, out var info))
            return;

        if (_settings.SortColumn == info.Sort)
        {
            _settings.SortDescending = !_settings.SortDescending;
        }
        else
        {
            _settings.SortColumn = info.Sort;
            // Lo mas util primero: los mas grandes / los mas recientes
            _settings.SortDescending = info.Sort is SortColumn.Size or SortColumn.Modified;
        }
        _settings.Save();
        UpdateSortHeaders();

        if (info.Sort is SortColumn.Size or SortColumn.Modified && ResultsList.Items.Count > 5000)
            ResultText.Text = $"Ordenando {ResultsList.Items.Count:N0} resultados (leyendo tamaños y fechas del disco)...";
        await SearchAsync();
    }

    void UpdateSortHeaders()
    {
        foreach (var (column, info) in _columns)
        {
            column.Header = info.Sort != _settings.SortColumn ? info.Title
                : info.Title + (_settings.SortDescending ? "  ▼" : "  ▲");
        }
    }

    void ReplaceResultsKeepingView(VirtualResultList list)
    {
        var scroll = FindDescendant<ScrollViewer>(ResultsList);
        double offset = scroll?.VerticalOffset ?? 0;
        var selected = Selected.ToList();

        ResultsList.ItemsSource = list;

        if (scroll is not null)
            Dispatcher.InvokeAsync(() => scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);

        // La seleccion se recupera cuando ya se cargaron las paginas visibles
        if (selected.Count > 0)
        {
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var row in selected)
                {
                    int i = list.IndexOf(row);
                    if (i >= 0) ResultsList.SelectedItems.Add(list[i]);
                }
            }, DispatcherPriority.ContextIdle);
        }
    }

    static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
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
                if (RootBox.Text == (_appliedRoot ?? "")) QueryBox.Focus();
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
        if (ResultsList.SelectedItem is ResultRow r)
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
                var first = (ResultRow)ResultsList.Items[0];
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
        if (sender is ListViewItem { Content: ResultRow row })
            Shell.Open(row.FullPath);
    }

    void OpenMenu_Click(object sender, RoutedEventArgs e) => OpenSelected();

    void ShowInFolderMenu_Click(object sender, RoutedEventArgs e) => ShowSelectedInFolder();

    void CopyPathMenu_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected.Select(r => r.FullPath));

    void CopyNameMenu_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected.Select(r => r.Name));

    IEnumerable<ResultRow> Selected => ResultsList.SelectedItems.Cast<ResultRow>();

    void OpenSelected()
    {
        // Limite para no abrir cientos de ventanas por accidente
        foreach (var r in Selected.Take(10))
            Shell.Open(r.FullPath);
    }

    void ShowSelectedInFolder()
    {
        if (ResultsList.SelectedItem is ResultRow r)
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
