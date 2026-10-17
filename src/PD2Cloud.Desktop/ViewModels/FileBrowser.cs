using System.Globalization;
using Avalonia.Media;
using PD2Cloud.Core;

namespace PD2Cloud.Desktop.ViewModels;

/// <summary>Which verified inventory the read-only file view is showing.</summary>
public enum FileScope { Local, Cloud }

/// <summary>Display-only projection of one inspected file. Never used for comparison or validation.</summary>
public sealed record FileRow(string Name, string TypeLabel, string SizeText, string ModifiedText,
    IBrush BadgeBackground, IBrush BadgeForeground, string Sha256);

public sealed record FileGroupHeader(string Title, string CountText, IReadOnlyList<FileRow> Rows);

/// <summary>
/// Presentation-only shaping of an already verified inventory: labels, sizes, timestamps and badges.
/// It reads display timestamps from the local folder but never hashes, compares or writes anything.
/// </summary>
public static class FileBrowser
{
    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".d2s", ".key", ".ma0", ".ma1", ".ma2", ".ma3", ".map", ".d2x", ".sss", ".ctl"
    };

    private static readonly Dictionary<string, (IBrush Background, IBrush Foreground)> BadgeBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D2S"] = (Brush("#2563EB"), Brush("#FFFFFF")),
        ["KEY"] = (Brush("#7C3AED"), Brush("#FFFFFF")),
        ["MA0"] = (Brush("#12A150"), Brush("#FFFFFF")),
        ["MA1"] = (Brush("#12A150"), Brush("#FFFFFF")),
        ["MA2"] = (Brush("#12A150"), Brush("#FFFFFF")),
        ["MA3"] = (Brush("#12A150"), Brush("#FFFFFF")),
        ["MAP"] = (Brush("#B7791F"), Brush("#FFFFFF")),
        ["SYS"] = (Brush("#3A4654"), Brush("#C6D0DC")),
        ["FILE"] = (Brush("#3A4654"), Brush("#C6D0DC")),
    };

    private static readonly (IBrush Background, IBrush Foreground) Fallback = (Brush("#3A4654"), Brush("#C6D0DC"));

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>Groups a verified inventory into the character/other sections shown by the files view.</summary>
    public static IReadOnlyList<FileGroupHeader> Group(IReadOnlyList<FileEntry> files, string? localRoot)
    {
        var groups = new List<FileGroupHeader>(2);
        var saves = new List<FileRow>();
        var other = new List<FileRow>();
        foreach (var file in files)
        {
            var row = Row(file, localRoot);
            (SaveExtensions.Contains(Extension(file.RelativePath)) ? saves : other).Add(row);
        }
        if (saves.Count > 0) groups.Add(new("Character Files", $"({saves.Count})", saves));
        if (other.Count > 0) groups.Add(new("Other Files", $"({other.Count})", other));
        return groups;
    }

    public static string CountText(int count) => $"{count} {(count == 1 ? "file" : "files")}";

    private static FileRow Row(FileEntry file, string? localRoot)
    {
        var label = TypeLabel(file.RelativePath);
        var (background, foreground) = BadgeBrushes.TryGetValue(label, out var brushes) ? brushes : Fallback;
        return new FileRow(file.RelativePath, label, FormatSize(file.SizeBytes), ModifiedText(file.RelativePath, localRoot), background, foreground, file.Sha256);
    }

    private static string Extension(string relativePath)
    {
        var name = relativePath.Split('/').Last();
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..];
    }

    private static string TypeLabel(string relativePath)
    {
        var name = relativePath.Split('/').Last();
        if (name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return "SYS";
        var extension = Extension(relativePath);
        return extension.Length > 1 ? extension[1..].ToUpperInvariant() : "FILE";
    }

    private static string ModifiedText(string relativePath, string? localRoot)
    {
        if (localRoot is null) return "—";
        try
        {
            var path = Path.Combine(localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "—";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "—";
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024) return Trim(kilobytes) + " KB";
        var megabytes = kilobytes / 1024d;
        if (megabytes < 1024) return Trim(megabytes) + " MB";
        return Trim(megabytes / 1024d) + " GB";
    }

    private static string Trim(double value) => (value >= 100 ? Math.Round(value) : Math.Round(value, 1))
        .ToString("0.#", CultureInfo.InvariantCulture);
}
