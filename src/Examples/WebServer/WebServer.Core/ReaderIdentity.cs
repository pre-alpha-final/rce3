namespace WebServer;

internal static class ReaderIdentity
{
    public static Guid LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "readerid");
        try
        {
            return Read(path);
        }
        catch (FileNotFoundException)
        {
            // Publish a complete file atomically; never overwrite another instance's ID.
            var temporary = Path.Combine(directory, $"readerid.{Guid.NewGuid():N}.tmp");
            try
            {
                var id = Guid.NewGuid();
                File.WriteAllText(temporary, id.ToString("D"));
                try
                {
                    File.Move(temporary, path, overwrite: false);
                    return id;
                }
                catch (IOException) when (File.Exists(path))
                {
                    return Read(path);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static Guid Read(string path)
    {
        if (Guid.TryParse(File.ReadAllText(path).Trim(), out var id))
            return id;

        throw new IOException($"Reader ID file '{path}' must contain a valid GUID. Restore it or delete it to start a new queue.");
    }
}
