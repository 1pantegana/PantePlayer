namespace LettoreVideo.Playlist;

public sealed class PlaylistItem(string path)
{
    public string Path { get; } = path;
    public string DisplayName { get; } = System.IO.Path.GetFileNameWithoutExtension(path);
    public string Extension { get; } = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    public override string ToString() => DisplayName;
}
