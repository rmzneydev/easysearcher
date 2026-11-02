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
    readonly SearchHistory _history = SearchHistory.Load();
    bool _applyingHistory;
    readonly Dictionary<GridViewColumn, (SortColumn Sort, string Title)> _columns;
    readonly GridView _detailsView;
    readonly ItemsPanelTemplate _detailsPanel;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    ISearchService? _service;
    bool _usingLocalEngine;
    EngineStatus? _status;
    bool _polling;
    string? _appliedRoot;
    bool _rootApplied;
    int _searchVersion;

    // Arrastrar y soltar
    Point _dragStart;
    bool _dragCandidate;
    ResultRow? _deferredClick;

    internal MainWindow(StartupArgs args)
    {
        InitializeComponent();
        RootBox.Text = args.Root ?? _settings.RootPath ?? "";
        QueryBox.Text = args.Query ?? "";
        SystemBox.IsChecked = _settings.IncludeSystem;
        HistoryEnabledBox.IsChecked = _settings.HistoryEnabled;

        _columns = new()
        {
            [NameColumn] = (SortColumn.Name, "Nombre"),
            [FolderColumn] = (SortColumn.Folder, "Carpeta"),
            [SizeColumn] = (SortColumn.Size, "Tamaño"),
            [ModifiedColumn] = (SortColumn.Modified, "Modificado"),
        };
        UpdateSortHeaders();

        _detailsView = (GridView)ResultsList.View;
        _detailsPanel = ResultsList.ItemsPanel;
        ViewBox.SelectedIndex = (int)_settings.ViewMode;
        ApplyViewMode();

        // Las miniaturas se piden en pixeles reales para que se vean nitidas con escalado de pantalla
        Loaded += (_, _) => ShellImages.Scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        DpiChanged += (_, e) => ShellImages.Scale = e.NewDpi.DpiScaleX;

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

    /// <summary>Otra instancia (p. ej. el clic derecho del Explorador) pidio abrir esta ventana con estos argumentos.</summary>
    internal async Task HandleArgsAsync(StartupArgs args)
    {
        BringToFront();
        if (args.Query is not null) QueryBox.Text = args.Query;
        if (args.Root is not null)
        {
            RootBox.Text = args.Root;
            // Si el indice aun no esta listo, la raiz se aplica cuando lo este
            _rootApplied = false;
            await ApplyRootAsync();
        }
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // Topmost por un instante: fuerza el frente cuando Activate solo hace parpadear la barra de tareas
        Topmost = true;
        Topmost = false;
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
            {
                await ApplyRootAsync();
            }
            else if (previous is { Ready: true })
            {
                if (previous.ChangeCounter != status.ChangeCounter)
                {
                    // Altas, bajas, renombres o movimientos: se repite la busqueda
                    await SearchAsync(preserveView: true);
                }
                else if (previous.DetailsCounter != status.DetailsCounter)
                {
                    // Solo cambiaron tamanos o fechas: si ordenan o filtran el resultado se repite la
                    // busqueda; si no, basta con refrescar las filas en pantalla
                    if (DependsOnDetails) await SearchAsync(preserveView: true);
                    else (ResultsList.ItemsSource as VirtualResultList)?.Refresh(rows: false);
                }
            }
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
        if (!status.DetailsReady && ServiceUpToDate)
            text += "  ·  leyendo tamaños y fechas...";
        if (!ServiceUpToDate)
            text += "   |   El servicio instalado es una version anterior: actualiza EasySearcher para filtrar por fecha, tamaño o tipo";
        StatusText.Text = text + source;

        DateBox.IsEnabled = ServiceUpToDate;
        DateBox.ToolTip = ServiceUpToDate ? "Fecha de modificacion" : "Requiere actualizar el servicio de EasySearcher";
        SizeBox.IsEnabled = ServiceUpToDate;
        SizeBox.ToolTip = ServiceUpToDate ? "Tamaño" : "Requiere actualizar el servicio de EasySearcher";
        // Las categorias (Imagenes, Videos...) tampoco las entiende un servicio anterior
        for (int i = (int)EntryKind.Images; i < KindBox.Items.Count; i++)
            ((ComboBoxItem)KindBox.Items[i]).IsEnabled = ServiceUpToDate;
        if (!ServiceUpToDate && KindBox.SelectedIndex >= (int)EntryKind.Images)
            KindBox.SelectedIndex = (int)EntryKind.All;
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
        // Al restaurar una busqueda del historial se cambian varios controles; se busca una vez al final
        if (_applyingHistory) return;
        if (!IsReady || !_rootApplied || _service is not { } service) return;

        int version = ++_searchVersion;
        string text = QueryBox.Text.Trim();
        string where = _appliedRoot ?? "todas las unidades";

        var preset = ServiceUpToDate ? (DatePreset)DateBox.SelectedIndex : DatePreset.Any;
        var (from, to) = DateFilter.GetRange(preset, FromDate.SelectedDate, ToDate.SelectedDate, DateTime.Now);
        string dateText = DateFilter.Describe(preset, FromDate.SelectedDate, ToDate.SelectedDate);
        if (dateText.Length > 0) where += $", {dateText}";

        var sizePreset = ServiceUpToDate ? (SizePreset)SizeBox.SelectedIndex : SizePreset.Any;
        var (minSize, maxSize) = SizeFilter.GetRange(sizePreset);
        string sizeText = SizeFilter.Describe(sizePreset);
        if (sizeText.Length > 0) where += $", {sizeText}";
        bool detailFilter = from is not null || to is not null || minSize is not null || maxSize is not null;

        // Sin texto solo se busca si hay filtro de fecha o tamano (p. ej. "lo de mas de 4 GB en D:\")
        if (text.Length == 0 && !detailFilter)
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
            preserveView ? current?.SearchId ?? 0 : 0,
            from,
            to,
            minSize,
            maxSize);

        try
        {
            if (!preserveView && detailFilter && _status?.DetailsReady != true)
                ResultText.Text = "Filtrando (todavia se estan leyendo tamaños y fechas)...";
            var response = await Task.Run(() => service.Search(request));
            // Si mientras tanto se lanzo otra busqueda, esta respuesta ya no interesa
            if (version != _searchVersion) return;

            ResultText.Text = $"{response.Total:N0} resultados en {where}  ·  {response.ElapsedMs:N0} ms";
            if (preserveView && response.Unchanged)
            {
                // Mismas entradas, pero pueden haber cambiado de nombre, carpeta o tamano
                current?.Refresh(rows: true);
                return;
            }

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
        // Con la lista de recientes abierta, lo que se escribe la filtra
        if (HistoryPopup.IsOpen) ShowHistory();
        await SearchAsync();
    }

    /// <summary>
    /// Guarda la busqueda actual con todo su contexto. Se llama al usar un resultado (abrir, abrir la
    /// carpeta, copiar o arrastrar): es la senal de que la busqueda sirvio.
    /// </summary>
    void RecordHistory()
    {
        if (!_settings.HistoryEnabled || !_rootApplied) return;
        var entry = new HistoryEntry(
            QueryBox.Text.Trim(),
            _appliedRoot,
            (EntryKind)KindBox.SelectedIndex,
            (DatePreset)DateBox.SelectedIndex,
            FromDate.SelectedDate,
            ToDate.SelectedDate,
            (SizePreset)SizeBox.SelectedIndex) { LastUsed = DateTime.Now };
        if (entry.Text.Length == 0 && entry.Date == DatePreset.Any && entry.Size == SizePreset.Any) return;
        _history.Add(entry);
    }

    /// <summary>Abre (o actualiza) la lista de busquedas recientes que contienen el texto escrito.</summary>
    void ShowHistory()
    {
        var entries = _history.Matching(QueryBox.Text.Trim());
        HistoryList.ItemsSource = entries;
        HistoryList.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Visibility = entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryEmpty.Text = !_settings.HistoryEnabled ? "El historial esta desactivado."
            : _history.Entries.Count == 0 ? "Las búsquedas aparecen aquí cuando abres, copias o arrastras un resultado."
            : "Ninguna búsqueda reciente contiene ese texto.";
        if (entries.Count > 0 && HistoryList.SelectedIndex < 0) HistoryList.SelectedIndex = 0;
        HistoryPopup.IsOpen = true;
    }

    /// <summary>Restaura una busqueda del historial (texto, carpeta y filtros) y la ejecuta.</summary>
    async Task ApplyHistoryAsync(HistoryEntry entry)
    {
        HistoryPopup.IsOpen = false;
        _applyingHistory = true;
        try
        {
            KindBox.SelectedIndex = (int)entry.Kind;
            DateBox.SelectedIndex = (int)entry.Date;
            FromDate.SelectedDate = entry.CustomFrom;
            ToDate.SelectedDate = entry.CustomTo;
            SizeBox.SelectedIndex = (int)entry.Size;
            QueryBox.Text = entry.Text;
            RootBox.Text = entry.Root ?? "";
        }
        finally
        {
            _applyingHistory = false;
        }
        QueryBox.Focus();
        QueryBox.CaretIndex = QueryBox.Text.Length;

        // Fuerza a validar la carpeta (pudo dejar de existir) y buscar
        _rootApplied = false;
        await ApplyRootAsync();
    }

    void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryPopup.IsOpen) HistoryPopup.IsOpen = false;
        else ShowHistory();
        QueryBox.Focus();
    }

    async void HistoryList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { Content: HistoryEntry entry })
            await ApplyHistoryAsync(entry);
    }

    async void HistoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && HistoryList.SelectedItem is HistoryEntry entry)
        {
            e.Handled = true;
            await ApplyHistoryAsync(entry);
        }
    }

    void RemoveSelectedHistory()
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry) return;
        int index = HistoryList.SelectedIndex;
        _history.Remove(entry);
        ShowHistory();
        HistoryList.SelectedIndex = Math.Min(index, HistoryList.Items.Count - 1);
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        ShowHistory();
    }

    void HistoryEnabledBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.HistoryEnabled = HistoryEnabledBox.IsChecked == true;
        _settings.Save();
        ShowHistory();
    }

    async void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await SearchAsync();

    /// <summary>Un servicio instalado anterior ignoraria sin avisar el filtro de fecha y las categorias.</summary>
    bool ServiceUpToDate => _status is null || _status.ProtocolVersion >= Protocol.Version;

    async void DateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Se dispara durante InitializeComponent, antes de que exista el panel
        if (CustomDatePanel is null) return;
        CustomDatePanel.Visibility = (DatePreset)DateBox.SelectedIndex == DatePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        await SearchAsync();
    }

    async void CustomDate_Changed(object? sender, SelectionChangedEventArgs e) => await SearchAsync();

    async void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await SearchAsync();

    /// <summary>El resultado depende de tamanos o fechas: hay que repetir la busqueda si cambian.</summary>
    bool DependsOnDetails =>
        _settings.SortColumn is SortColumn.Size or SortColumn.Modified ||
        DateBox.SelectedIndex != (int)DatePreset.Any ||
        SizeBox.SelectedIndex != (int)SizePreset.Any;

    void ViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Se dispara durante InitializeComponent, antes de que exista la lista
        if (ResultsList is null || _detailsView is null) return;
        _settings.ViewMode = (ViewMode)ViewBox.SelectedIndex;
        _settings.Save();
        ApplyViewMode();
    }

    /// <summary>
    /// Detalles usa la GridView con columnas; las vistas de iconos quitan la GridView y muestran cada
    /// resultado con su miniatura en una cuadricula virtualizada.
    /// </summary>
    void ApplyViewMode()
    {
        switch (_settings.ViewMode)
        {
            case ViewMode.MediumIcons:
            case ViewMode.LargeIcons:
                bool large = _settings.ViewMode == ViewMode.LargeIcons;
                ResultsList.View = null;
                ResultsList.ItemTemplate = (DataTemplate)FindResource(large ? "LargeIconsTemplate" : "MediumIconsTemplate");
                ResultsList.ItemsPanel = (ItemsPanelTemplate)FindResource(large ? "LargeIconsPanel" : "MediumIconsPanel");
                break;
            default:
                ResultsList.ItemTemplate = null;
                ResultsList.ItemsPanel = _detailsPanel;
                ResultsList.View = _detailsView;
                break;
        }
    }

    async void SystemBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeSystem = SystemBox.IsChecked == true;
        _settings.Save();
        await SearchAsync();
    }

    async void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Con la lista de recientes abierta, las flechas, Enter y Esc son para ella
        if (HistoryPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down or Key.Up when HistoryList.Items.Count > 0:
                    int step = e.Key == Key.Down ? 1 : -1;
                    HistoryList.SelectedIndex = Math.Clamp(HistoryList.SelectedIndex + step, 0, HistoryList.Items.Count - 1);
                    HistoryList.ScrollIntoView(HistoryList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Enter when HistoryList.SelectedItem is HistoryEntry entry:
                    e.Handled = true;
                    await ApplyHistoryAsync(entry);
                    return;
                case Key.Delete when Keyboard.Modifiers == ModifierKeys.Shift:
                    RemoveSelectedHistory();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    HistoryPopup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            // Con el campo vacio, abajo abre las busquedas recientes
            case Key.Down when QueryBox.Text.Length == 0:
                ShowHistory();
                e.Handled = true;
                break;
            case Key.Down when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = 0;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when ResultsList.Items.Count > 0:
                var first = (ResultRow)ResultsList.Items[0];
                RecordHistory();
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

    void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        _dragStart = e.GetPosition(ResultsList);
        _dragCandidate = item is not null;
        _deferredClick = null;

        // Clic simple sobre una fila de una seleccion multiple: se conserva la seleccion para poder
        // arrastrarla entera. Si al final no se arrastra, el clic se aplica al soltar el boton.
        if (item is { IsSelected: true, Content: ResultRow row } && e.ClickCount == 1 &&
            ResultsList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferredClick = row;
            item.Focus();
            e.Handled = true;
        }
    }

    void ResultsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(ResultsList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = false;
        _deferredClick = null;

        var paths = Selected.Where(r => r.Name.Length > 0).Select(r => r.FullPath).ToArray();
        if (paths.Length == 0) return;

        // Como archivos (Explorador, correo, navegadores...) y como texto para lo que solo acepta texto
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine, paths));

        // Copiar, mover o acceso directo: decide el destino, igual que entre ventanas del Explorador
        var effect = DragDrop.DoDragDrop(ResultsList, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        if (effect != DragDropEffects.None) RecordHistory();
    }

    void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredClick is { } row)
        {
            ResultsList.SelectedItems.Clear();
            ResultsList.SelectedItem = row;
        }
        _deferredClick = null;
        _dragCandidate = false;
    }

    static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    void ResultItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { Content: ResultRow row })
        {
            RecordHistory();
            Shell.Open(row.FullPath);
        }
    }

    void OpenMenu_Click(object sender, RoutedEventArgs e) => OpenSelected();

    void ShowInFolderMenu_Click(object sender, RoutedEventArgs e) => ShowSelectedInFolder();

    void CopyPathMenu_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected.Select(r => r.FullPath));

    void CopyNameMenu_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Selected.Select(r => r.Name));

    IEnumerable<ResultRow> Selected => ResultsList.SelectedItems.Cast<ResultRow>();

    void OpenSelected()
    {
        if (!Selected.Any()) return;
        RecordHistory();
        // Limite para no abrir cientos de ventanas por accidente
        foreach (var r in Selected.Take(10))
            Shell.Open(r.FullPath);
    }

    void ShowSelectedInFolder()
    {
        if (ResultsList.SelectedItem is not ResultRow r) return;
        RecordHistory();
        Shell.ShowInFolder(r.FullPath);
    }

    void CopyToClipboard(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;
        RecordHistory();
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
