namespace Resesh.Core.Storage;

internal static class AtomicFile
{
    public static void Write(string path, string contents, bool preserveBackup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        if (File.Exists(path))
            File.Replace(temporary, path, preserveBackup ? null : path + ".bak");
        else
            File.Move(temporary, path);
    }
}
