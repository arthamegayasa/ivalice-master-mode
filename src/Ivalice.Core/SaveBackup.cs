using System.Globalization;

namespace Ivalice.Core;

/// <param name="Directory">The folder the copies went to.</param>
/// <param name="Files">How many save files were copied.</param>
public sealed record SaveBackupResult(string Directory, int Files);

/// <summary>
/// Copies the game's save files aside before something changes what the next save will hold.
/// The game keeps each save as a .png file under a folder per store account.
/// </summary>
public static class SaveBackup
{
    public const int Keep = 20;
    private const string NameFormat = "yyyyMMdd-HHmmss";

    public static string DefaultSaveDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "FINAL FANTASY TACTICS - The Ivalice Chronicles");

    /// <summary>
    /// Copies every .png under <paramref name="saveDirectory"/> into a new folder named after the time,
    /// keeping the sub-folders, then removes the oldest backups beyond <paramref name="keep"/>.
    /// </summary>
    /// <exception cref="ModOperationException">The save folder does not exist or could not be copied.</exception>
    public static SaveBackupResult Create(string saveDirectory, string backupsDirectory, DateTime now, int keep = Keep)
    {
        if (!Directory.Exists(saveDirectory))
            throw new ModOperationException("Folder save game tidak ditemukan: " + saveDirectory);

        try
        {
            Directory.CreateDirectory(backupsDirectory);
            var name = now.ToString(NameFormat, CultureInfo.InvariantCulture);
            var target = Path.Combine(backupsDirectory, name);
            for (var n = 2; Directory.Exists(target); n++) target = Path.Combine(backupsDirectory, $"{name}-{n}");
            Directory.CreateDirectory(target);

            var files = 0;
            foreach (var file in Directory.EnumerateFiles(saveDirectory, "*.png", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(target, Path.GetRelativePath(saveDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, overwrite: false);
                files++;
            }

            Prune(backupsDirectory, keep);
            return new SaveBackupResult(target, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException("Cadangan save tidak bisa dibuat: " + e.Message);
        }
    }

    /// <summary>Removes the oldest backup folders. Folders this class did not name are never touched.</summary>
    private static void Prune(string backupsDirectory, int keep)
    {
        var backups = Directory.EnumerateDirectories(backupsDirectory)
            .Where(d => IsBackupName(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d)[..NameFormat.Length], StringComparer.Ordinal)
            .ThenByDescending(d => Path.GetFileName(d).Length)
            .ThenByDescending(d => Path.GetFileName(d), StringComparer.Ordinal);
        foreach (var old in backups.Skip(Math.Max(keep, 1))) Directory.Delete(old, recursive: true);
    }

    private static bool IsBackupName(string name)
    {
        if (name.Length < NameFormat.Length) return false;
        if (!DateTime.TryParseExact(name[..NameFormat.Length], NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;
        var suffix = name[NameFormat.Length..];
        return suffix.Length == 0 || (suffix[0] == '-' && suffix.Length > 1 && suffix[1..].All(char.IsAsciiDigit));
    }
}
