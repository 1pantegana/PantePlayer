using System.IO;
using System.Runtime.InteropServices;

namespace LettoreVideo.Playlist;

public static class VideoFileScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv",
        ".m4v", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".ogv", ".3gp", ".divx", ".vob",
    };

    /// <summary>
    /// Lists the video files directly inside <paramref name="folder"/>, ordered the
    /// way Explorer does (so "ep2" sorts before "ep10").
    /// </summary>
    public static List<PlaylistItem> Scan(string folder)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .ToList();

        files.Sort(NaturalComparer.Instance);

        return files.Select(path => new PlaylistItem(path)).ToList();
    }

    /// <summary>
    /// Natural ("1, 2, 10") ordering, delegating to the same shell API Explorer uses
    /// so the playlist matches what the user sees in the folder.
    /// </summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y) =>
            StrCmpLogicalW(Path.GetFileName(x) ?? string.Empty, Path.GetFileName(y) ?? string.Empty);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);
    }
}
