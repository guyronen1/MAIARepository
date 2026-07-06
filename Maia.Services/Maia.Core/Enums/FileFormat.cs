namespace Maia.Core.Enums;

/// <summary>
/// Content format of an input data file, used by FileContent scans to pick the
/// matching IFileContentExtractor. One value per shipped extractor.
/// v1 ships XML only; CSV / JSON / Excel / fixed-width are v2 (add the enum
/// value AND the extractor implementation together).
/// </summary>
public enum FileFormat
{
    Xml = 0
}
