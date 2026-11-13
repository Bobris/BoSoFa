namespace BoSoFa.Daemon;

/// <summary>Files only this user may read: keys, and configurations that hold secrets.</summary>
public static class PrivateFile
{
    const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes the text atomically, created readable by this user alone (on Windows the folder's ACL rules).</summary>
    public static void Write(string path, string text)
    {
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = Owner;
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(text);
        File.Move(temporary, path, true);
    }

    /// <summary>Creates the folder, which on Unix only this user may list or enter.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, Owner | UnixFileMode.UserExecute);
    }
}
