namespace EasySearcher.Core;

public enum EntryKind
{
    All,
    Files,
    Directories,

    // Archivos por categoria (extension); ver FileCategories. Protocolo 3.
    Images,
    Videos,
    Audio,
    Documents,
}
