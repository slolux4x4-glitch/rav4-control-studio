namespace Obd2.Core.Obd;

/// <summary>
/// Reading files the user (or anyone with access to the folder) drops into the Codes and Parameters folders. Those
/// folders are read automatically at startup, so a stray huge file must be skipped with a message instead of
/// freezing the app or using up its memory.
/// </summary>
internal static class UserFiles
{
    /// <summary>Largest file read: far bigger than any real code list or parameter file.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    public static void CheckSize(string path)
    {
        long length = new FileInfo(path).Length;
        if (length > MaxBytes)
            throw new IOException($"the file is {length / (1024 * 1024):N0} MB; files over {MaxBytes / (1024 * 1024)} MB are not read");
    }

    public static string ReadAllText(string path)
    {
        CheckSize(path);
        return File.ReadAllText(path);
    }
}
