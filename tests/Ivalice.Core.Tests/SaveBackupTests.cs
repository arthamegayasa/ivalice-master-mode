using Ivalice.Core;

namespace Ivalice.Core.Tests;

public sealed class SaveBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ivalice-backup-tests-" + Guid.NewGuid().ToString("N"));
    private string Saves => Path.Combine(_root, "saves");
    private string Backups => Path.Combine(_root, "backups");
    private static readonly DateTime Noon = new(2026, 11, 20, 12, 0, 0);

    public SaveBackupTests()
    {
        Write(Path.Combine(Saves, "Steam", "123", "enhanced.png"), "manual saves");
        Write(Path.Combine(Saves, "Steam", "123", "autoenhanced.png"), "autosave");
        Write(Path.Combine(Saves, "Steam", "123", "steam_autocloud.vdf"), "not a save");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string[] BackupNames() => Directory.GetDirectories(Backups).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public void A_backup_copies_every_save_file_and_keeps_its_folder()
    {
        var result = SaveBackup.Create(Saves, Backups, Noon);

        Assert.Equal(2, result.Files);
        Assert.Equal(Path.Combine(Backups, "20261120-120000"), result.Directory);
        Assert.Equal("manual saves", File.ReadAllText(Path.Combine(result.Directory, "Steam", "123", "enhanced.png")));
        Assert.Equal("autosave", File.ReadAllText(Path.Combine(result.Directory, "Steam", "123", "autoenhanced.png")));
        Assert.False(File.Exists(Path.Combine(result.Directory, "Steam", "123", "steam_autocloud.vdf")));
    }

    [Fact]
    public void A_backup_leaves_the_save_folder_as_it_was()
    {
        SaveBackup.Create(Saves, Backups, Noon);

        Assert.Equal(3, Directory.GetFiles(Saves, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("manual saves", File.ReadAllText(Path.Combine(Saves, "Steam", "123", "enhanced.png")));
    }

    [Fact]
    public void Two_backups_in_the_same_second_do_not_overwrite_each_other()
    {
        var first = SaveBackup.Create(Saves, Backups, Noon);
        var second = SaveBackup.Create(Saves, Backups, Noon);

        Assert.NotEqual(first.Directory, second.Directory);
        Assert.Equal(["20261120-120000", "20261120-120000-2"], BackupNames());
    }

    [Fact]
    public void Only_the_newest_backups_are_kept()
    {
        for (var minute = 0; minute < 5; minute++) SaveBackup.Create(Saves, Backups, Noon.AddMinutes(minute), keep: 3);

        Assert.Equal(["20261120-120200", "20261120-120300", "20261120-120400"], BackupNames());
    }

    [Fact]
    public void Twenty_backups_are_kept_by_default()
    {
        for (var minute = 0; minute < 22; minute++) SaveBackup.Create(Saves, Backups, Noon.AddMinutes(minute));

        Assert.Equal(20, BackupNames().Length);
        Assert.DoesNotContain("20261120-120100", BackupNames());
        Assert.Contains("20261120-122100", BackupNames());
    }

    [Fact]
    public void Folders_the_backup_did_not_create_are_never_removed()
    {
        Directory.CreateDirectory(Path.Combine(Backups, "my own copy"));
        Directory.CreateDirectory(Path.Combine(Backups, "20261120-notatime"));

        for (var minute = 0; minute < 4; minute++) SaveBackup.Create(Saves, Backups, Noon.AddMinutes(minute), keep: 1);

        Assert.Equal(["20261120-120300", "20261120-notatime", "my own copy"], BackupNames());
    }

    [Fact]
    public void A_missing_save_folder_is_refused_with_a_message_for_the_player()
    {
        var error = Assert.Throws<ModOperationException>(() => SaveBackup.Create(Path.Combine(_root, "nowhere"), Backups, Noon));

        Assert.Contains("tidak ditemukan", error.Message);
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void A_save_folder_without_saves_still_gets_an_empty_backup()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(0, SaveBackup.Create(empty, Backups, Noon).Files);
    }
}
