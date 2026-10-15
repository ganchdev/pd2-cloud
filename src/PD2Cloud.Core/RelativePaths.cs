using System.Text;

namespace PD2Cloud.Core;

public static class RelativePaths
{
    // NFC canonical paths are portable; filenames on disk are never renamed.
    public static string Canonical(string path) => path.Normalize(NormalizationForm.FormC);
    public static string CollisionKey(string path) => Canonical(path).ToUpperInvariant();

    public static bool IsSafe(string path) => !string.IsNullOrEmpty(path) && path.Split('/').All(IsSafeSegment);

    private static bool IsSafeSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]));
    }
}
