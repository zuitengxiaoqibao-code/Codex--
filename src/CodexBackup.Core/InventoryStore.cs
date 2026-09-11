using Microsoft.Data.Sqlite;

namespace CodexBackup.Core;

internal static class InventoryStore
{
    internal const int MaxEntries = 1_000_000;
    internal static void Write(string path, IReadOnlyList<FileRecord> records)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; CREATE TABLE files(id TEXT PRIMARY KEY, root_id TEXT NOT NULL, relative_path TEXT NOT NULL, is_directory INTEGER NOT NULL, length INTEGER NOT NULL, sha256 TEXT NOT NULL, last_write INTEGER NOT NULL, attributes INTEGER NOT NULL);";
        command.ExecuteNonQuery();
        using var tx = db.BeginTransaction();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO files VALUES($id,$root,$path,$dir,$len,$hash,$time,$attrs)";
        foreach (var key in new[] { "$id", "$root", "$path", "$dir", "$len", "$hash", "$time", "$attrs" }) command.Parameters.Add(new SqliteParameter(key, ""));
        foreach (var f in records)
        {
            command.Parameters[0].Value = f.Id; command.Parameters[1].Value = f.RootId; command.Parameters[2].Value = f.RelativePath;
            command.Parameters[3].Value = f.IsDirectory ? 1 : 0; command.Parameters[4].Value = f.Length; command.Parameters[5].Value = f.Sha256;
            command.Parameters[6].Value = f.LastWriteUtcTicks; command.Parameters[7].Value = f.Attributes; command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal static List<FileRecord> Read(string path, CancellationToken ct)
    {
        PathSafety.RejectReparseAncestors(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 512L * 1024 * 1024) throw new BackupException("备份文件清单缺失或超过 512 MiB 安全上限。");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = new Uri(path).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name='files'";
        if (!Equals(command.ExecuteScalar(), "table")) throw new BackupException("文件清单不是受支持的数据表。");
        command.CommandText = $"SELECT id,root_id,relative_path,is_directory,length,sha256,last_write,attributes FROM files LIMIT {MaxEntries + 1}";
        using var registration = ct.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var list = new List<FileRecord>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (list.Count >= MaxEntries) throw new BackupException("清单超过一百万条安全上限。");
            list.Add(new FileRecord { Id = reader.GetString(0), RootId = reader.GetString(1), RelativePath = reader.GetString(2), IsDirectory = reader.GetInt32(3) == 1,
                Length = reader.GetInt64(4), Sha256 = reader.GetString(5), LastWriteUtcTicks = reader.GetInt64(6), Attributes = reader.GetInt32(7) });
        }
        return list;
    }
}
