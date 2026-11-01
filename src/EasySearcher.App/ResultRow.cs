using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using EasySearcher.Core.Service;

namespace EasySearcher.App;

/// <summary>
/// Una fila de resultados. El tamano y la fecha llegan despues que el resto (se leen del disco en
/// segundo plano), por eso notifican sus cambios. Dos filas son iguales si apuntan a la misma entrada.
/// </summary>
sealed class ResultRow(RowData data) : INotifyPropertyChanged, IEquatable<ResultRow>
{
    long? _size;
    DateTime? _modified;

    public char Drive { get; } = data.Drive;
    public int Entry { get; } = data.Entry;
    public string Name { get; private set; } = data.Name;
    public string DirectoryPath { get; private set; } = data.Directory;
    public bool IsDirectory { get; } = data.IsDirectory;
    public string FullPath => Path.Join(DirectoryPath, Name);

    /// <summary>La entrada se renombro o se movio: actualiza la fila en pantalla sin rehacer la lista.</summary>
    public void Update(RowData data)
    {
        if (data.Drive != Drive || data.Entry != Entry) return;
        if (data.Name == Name && data.Directory == DirectoryPath) return;

        Name = data.Name;
        DirectoryPath = data.Directory;
        // La ruta cambio: tambien el icono y la miniatura, que se buscan por ruta
        foreach (var property in new[] { nameof(Name), nameof(DirectoryPath), nameof(FullPath),
                     nameof(SmallIcon), nameof(MediumThumbnail), nameof(LargeThumbnail) })
            NotifyChanged(property);
    }

    public long? Size
    {
        get => _size;
        set => Set(ref _size, value);
    }

    public DateTime? Modified
    {
        get => _modified;
        set => Set(ref _modified, value);
    }

    // Las imagenes no se guardan en la fila (miles de filas cacheadas ocuparian mucha memoria): se leen de la
    // cache de ShellImages, que avisa con NotifyChanged cuando una termina de cargarse
    public ImageSource? SmallIcon => ShellImages.Get(this, ShellImageKind.SmallIcon, nameof(SmallIcon));
    public ImageSource? MediumThumbnail => ShellImages.Get(this, ShellImageKind.MediumThumbnail, nameof(MediumThumbnail));
    public ImageSource? LargeThumbnail => ShellImages.Get(this, ShellImageKind.LargeThumbnail, nameof(LargeThumbnail));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        NotifyChanged(name!);
    }

    // Lo usan la accesibilidad (lectores de pantalla, UI Automation) y la busqueda por teclado de WPF
    public override string ToString() => Name;

    public bool Equals(ResultRow? other) => other is not null && other.Drive == Drive && other.Entry == Entry;

    public override bool Equals(object? obj) => Equals(obj as ResultRow);

    public override int GetHashCode() => HashCode.Combine(Drive, Entry);
}
