namespace Dom;

/// <summary>Playlist covers on disk under <c>Dom:CoverDir</c>. The store keeps a file name; this owns the files.</summary>
public sealed class CoverFiles(IConfiguration configuration)
{
    private string Directory => configuration["Dom:CoverDir"] ?? "covers";

    /// <returns>The file name the playlist now records: one per playlist, so a second upload replaces the first.</returns>
    public async Task<string> SaveAsync(string playlistId, string extension, IFormFile file,
        CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = $"{playlistId}.{extension}";
        await using (var target = File.Create(Path.Combine(Directory, name)))
        {
            await file.CopyToAsync(target, cancellationToken);
        }

        return name;
    }

    /// <returns>The cover's full path, or <c>null</c> when its file has gone.</returns>
    public string? Find(string coverFile)
    {
        var path = Path.Combine(Directory, coverFile);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>A cover nothing points at any more. A failure here is a stray file, never an error.</summary>
    public void Forget(string coverFile)
    {
        try
        {
            File.Delete(Path.Combine(Directory, coverFile));
        }
        catch (IOException)
        {
            // ponytail: a leftover cover costs a few kilobytes; failing the delete would cost the user
        }
    }
}
