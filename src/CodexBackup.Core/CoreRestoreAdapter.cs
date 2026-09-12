using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Globalization;

namespace CodexBackup.Core;

/// <summary>Offline adaptation of a verified staging copy. Never call on a live Codex home.</summary>
public sealed class CoreRestoreAdapter
{
    private const string Disabled = ".codex-backup-disabled";
    private static readonly HashSet<string> Tables = new(StringComparer.Ordinal)
    { "_sqlx_migrations", "threads", "thread_dynamic_tools", "backfill_state", "thread_spawn_edges", "remote_control_enrollments", "external_agent_config_imports", "thread_sections", "rollout_migration_state", "rollout_migration_skipped_rollouts", "projects", "project_roots", "project_idempotency_keys", "thread_artifacts" };
    public static bool IsCandidate(BackupRoot root, IReadOnlyList<FileRecord> records) => root.IsDirectory && root.Kind==SourceKind.Core &&
        records.Any(f => f.RootId == root.Id && (f.RelativePath.Equals("state_5.sqlite",StringComparison.OrdinalIgnoreCase) || f.RelativePath.Replace('\\','/').StartsWith("sessions/",StringComparison.OrdinalIgnoreCase)));

    public async Task<List<string>> PrepareAsync(string stagingRoot, string originalCorePath, IReadOnlyDictionary<string,string> pathMappings, CancellationToken ct = default)
    {
        PathSafety.RejectReparseAncestors(stagingRoot);
        if (Path.GetFullPath(stagingRoot).TrimEnd('\\','/').Equals(Path.GetFullPath(originalCorePath).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))
            throw new BackupException("Core 适配只能修改独立暂存副本。");
        var disabled = Path.Combine(stagingRoot,Disabled);
        if (Path.Exists(disabled)) throw new BackupException("暂存目录包含保留的禁用目录，拒绝合并。");
        var notes = new List<string>();
        static string Normalize(string value)
        {
            if(value.StartsWith("\\\\?\\",StringComparison.Ordinal) && value.Length>6 && char.IsAsciiLetter(value[4]) && value[5]==':')value=value[4..];
            if(!Path.IsPathFullyQualified(value) || value.StartsWith("\\\\",StringComparison.Ordinal) || value.StartsWith("//",StringComparison.Ordinal)) throw new BackupException("Core 路径映射必须是完整本地路径。");
            return Path.GetFullPath(value).TrimEnd('\\','/');
        }
        var mappings = pathMappings.Select(p=>new KeyValuePair<string,string>(Normalize(p.Key),Normalize(p.Value))).OrderByDescending(p=>p.Key.Length).ToArray();
        if(mappings.Select(p=>p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=mappings.Length) throw new BackupException("Core 路径映射含重复的规范化源。");
        var pathNoteCount=0;
        void PathNote(string value) { if(pathNoteCount++<500) notes.Add(value); else if(pathNoteCount==501) notes.Add("路径说明超过 500 条，后续说明已省略；映射规则仍应用于所有受支持记录。"); }
        string Map(string value)
        {
            if(value.StartsWith("\\\\?\\",StringComparison.Ordinal) && value.Length>6 && char.IsAsciiLetter(value[4]) && value[5]==':')value=value[4..];
            var comparable=value.Replace('/','\\');
            foreach(var pair in mappings)
            {
                var prefix=pair.Key.TrimEnd('\\','/');
                if(comparable.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && (value.Length==prefix.Length || value[prefix.Length] is '\\' or '/'))
                {
                    var target=value.Contains('/') && !value.Contains('\\')?pair.Value.Replace('\\','/'):pair.Value;
                    var result=target+value[prefix.Length..];
                    if(result!=value) PathNote($"已重连路径：{value} → {result}");
                    return result;
                }
            }
            if(!string.IsNullOrWhiteSpace(value)) PathNote($"路径无映射，保留原值，需手动确认：{value}");
            return value;
        }
        var state=Path.Combine(stagingRoot,"state_5.sqlite");
        if(File.Exists(state)) await AdaptDatabase(state,Map,notes,ct);
        // Only these passive top-level resources remain active. Unknown resources are kept disabled.
        var passive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "state_5.sqlite","state_5.sqlite-wal","state_5.sqlite-shm","thread_history_1.sqlite","thread_history_1.sqlite-wal","thread_history_1.sqlite-shm","sessions","archived_sessions","session_index.jsonl","history.jsonl",".codex-global-state.json","memories","worktrees" };
        foreach(var entry in Directory.EnumerateFileSystemEntries(stagingRoot).ToArray())
        {
            ct.ThrowIfCancellationRequested(); PathSafety.RejectReparseAncestors(entry);
            var name=Path.GetFileName(entry);
            if(passive.Contains(name)) continue;
            Directory.CreateDirectory(disabled);
            var target=Path.Combine(disabled,name);
            if(Directory.Exists(entry)) Directory.Move(entry,target); else File.Move(entry,target);
            notes.Add($"已禁用并保留：{name}（位于 {Disabled}，不会自动启用）");
        }
        // Global state may contain feature toggles and future integrations: retain only known root UI fields.
        var global=Path.Combine(stagingRoot,".codex-global-state.json");
        if(File.Exists(global))
        {
            var node=FileIO.ReadJson<JsonObject>(global);
            Directory.CreateDirectory(disabled); File.Copy(global,Path.Combine(disabled,".codex-global-state.json"));
            var clean=new JsonObject();
            static bool Strings(JsonNode? value) => value is JsonArray array && array.All(x=>x is JsonValue v && v.TryGetValue<string>(out _));
            if(node["local-projects"] is JsonArray projects)
            {
                var kept=new JsonArray();
                foreach(var entry in projects)
                {
                    if(entry is not JsonObject project || project["id"] is not JsonValue id || !id.TryGetValue<string>(out _) || project["name"] is not JsonValue name || !name.TryGetValue<string>(out _) || !Strings(project["rootPaths"])) throw new BackupException("local-projects 结构不受支持，拒绝丢失项目关联。");
                    kept.Add(new JsonObject { ["id"]=id.DeepClone(),["name"]=name.DeepClone(),["rootPaths"]=new JsonArray(((JsonArray)project["rootPaths"]!).Select(x=>(JsonNode?)JsonValue.Create(Map(x!.GetValue<string>()))).ToArray()) });
                }
                clean["local-projects"]=kept;
            }
            else if(node["local-projects"] is JsonObject projectMap)
            {
                var kept=new JsonObject();
                foreach(var pair in projectMap)
                {
                    if(pair.Value is not JsonObject project || !Strings(project["rootPaths"]))throw new BackupException("local-projects 项目关联结构不受支持。");
                    var entry=new JsonObject{["rootPaths"]=new JsonArray(((JsonArray)project["rootPaths"]!).Select(x=>(JsonNode?)JsonValue.Create(Map(x!.GetValue<string>()))).ToArray())};
                    foreach(var field in new[]{"id","name"})if(project[field] is JsonValue v && v.TryGetValue<string>(out _))entry[field]=v.DeepClone();
                    kept[pair.Key]=entry;
                }
                clean["local-projects"]=kept;
            }
            else if(node.ContainsKey("local-projects")) throw new BackupException("local-projects 结构不受支持。");
            if(node["thread-projectless-output-directories"] is JsonObject outputs)
            {
                var kept=new JsonObject();foreach(var pair in outputs){if(pair.Value is not JsonValue v || !v.TryGetValue<string>(out var p))throw new BackupException("无项目会话输出目录结构不受支持。");kept[pair.Key]=Map(p);}clean["thread-projectless-output-directories"]=kept;
            }
            if(node["electron-persisted-atom-state"] is JsonObject atoms)
            {
                var kept=new JsonObject();
                foreach(var pair in atoms.Where(p=>p.Key.StartsWith("thread-workspace-state-v1:",StringComparison.Ordinal)))
                {
                    if(pair.Value is not JsonObject workspaceState)throw new BackupException("会话工作区状态结构不受支持。");
                    var updated=new JsonObject();
                    foreach(var phase in new[]{"applied","pending"})
                    {
                        if(workspaceState[phase] is not JsonObject workspace)throw new BackupException("会话工作区阶段结构不受支持。");
                        var copy=new JsonObject();
                        if(workspace["cwd"] is JsonValue cwd && cwd.TryGetValue<string>(out var path))copy["cwd"]=Map(path);
                        else throw new BackupException("会话工作区 cwd 结构不受支持。");
                        foreach(var field in new[]{"projectSources","runtimeWorkspaceRoots"})
                        {
                            if(!Strings(workspace[field]))throw new BackupException("会话工作区路径列表结构不受支持。");
                            copy[field]=new JsonArray(((JsonArray)workspace[field]!).Select(x=>(JsonNode?)JsonValue.Create(Map(x!.GetValue<string>()))).ToArray());
                        }
                        updated[phase]=copy;
                    }
                    if(workspaceState["project"] is JsonObject project)
                    {
                        var copy=new JsonObject();foreach(var field in new[]{"projectId","projectKind"})if(project[field] is JsonValue v && v.TryGetValue<string>(out _))copy[field]=v.DeepClone();updated["project"]=copy;
                    }
                    if(workspaceState["revision"] is JsonValue revision && revision.TryGetValue<string>(out _))updated["revision"]=revision.DeepClone();
                    kept[pair.Key]=updated;
                }
                clean["electron-persisted-atom-state"]=kept;
            }
            if(node.ContainsKey("project-order")) {if(!Strings(node["project-order"]))throw new BackupException("project-order 结构不受支持。");clean["project-order"]=node["project-order"]!.DeepClone();}
            if(node["thread-workspace-root"] is JsonObject associations)
            {
                var kept=new JsonObject();
                foreach(var pair in associations) {if(pair.Value is not JsonValue v || !v.TryGetValue<string>(out var root))throw new BackupException("thread-workspace-root 结构不受支持。"); kept[pair.Key]=Map(root);}
                clean["thread-workspace-root"]=kept;
            }
            else if(node.ContainsKey("thread-workspace-root"))throw new BackupException("thread-workspace-root 结构不受支持。");
            foreach(var key in new[]{"electron-saved-workspace-roots","active-workspace-roots"})
                if(node[key] is JsonArray roots) clean[key]=new JsonArray(roots.Select(x=>(JsonNode?)JsonValue.Create(Map(x?.GetValue<string>() ?? ""))).ToArray());
            if(node["electron-workspace-root-labels"] is JsonObject labels)
            {
                var remapped=new JsonObject();
                foreach(var pair in labels) { var key=Map(pair.Key); if(remapped.ContainsKey(key)) throw new BackupException("工作区映射产生重复路径。"); remapped[key]=pair.Value?.DeepClone(); }
                clean["electron-workspace-root-labels"]=remapped;
            }
            FileIO.WriteJsonDurable(global,clean);
            notes.Add("全局状态仅恢复已知工作区路径和标签，其余设置已禁用保留。");
        }
        var pointer=Path.Combine(stagingRoot,"memories","obsidian-vault-path.txt");
        if(File.Exists(pointer))
        {
            PathSafety.RejectReparseAncestors(pointer);
            if(new FileInfo(pointer).Length>32768) throw new BackupException("记忆库路径文件异常。");
            var old=await File.ReadAllTextAsync(pointer,Encoding.UTF8,ct);
            await File.WriteAllTextAsync(pointer,Map(old.Trim())+Environment.NewLine,new UTF8Encoding(false,true),ct);
        }
        notes.Add("Core 历史已适配；插件、技能、自动化、配置及凭据已隔离。请重新登录并逐项审核后启用集成。历史 JSONL 消息未改写。");
        return notes.Distinct().ToList();
    }

    private static async Task AdaptDatabase(string path, Func<string,string> map, List<string> notes,CancellationToken ct)
    {
        PathSafety.RejectReparseAncestors(path);
        if(new FileInfo(path).Length>2L*1024*1024*1024) throw new BackupException("Core 数据库超过 2 GiB 适配上限，请手动迁移。");
        SQLitePCL.Batteries_V2.Init();
        await using var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, Mode=SqliteOpenMode.ReadWrite, Pooling=false }.ToString());
        await db.OpenAsync(ct);
        async Task Run(string sql) { using var c=db.CreateCommand();c.CommandText=sql;await c.ExecuteNonQueryAsync(ct); }
        await Run("PRAGMA trusted_schema=OFF;");
        // Exact schema + migration metadata observed read-only on 2026-09-11, not an app-version guarantee.
        using(var c=db.CreateCommand()) {c.CommandText="PRAGMA user_version";if(Convert.ToInt64(await c.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)!=0)throw new BackupException("Core user_version 未受支持，请使用隔离恢复。");}
        var schemaRows=new List<string>();
        using(var c=db.CreateCommand())
        {
            c.CommandText="SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' ORDER BY type,name";
            using var r=await c.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                if(schemaRows.Count>=1000)throw new BackupException("Core schema 超过安全上限。");
                schemaRows.Add(string.Join("\u001f",Enumerable.Range(0,4).Select(i=>r.IsDBNull(i)?"":r.GetString(i).Replace("\r\n","\n",StringComparison.Ordinal))));
            }
        }
        static string Digest(IEnumerable<string> values)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",values))));
        if(Digest(schemaRows)!="0C177751B309087C70BB6390E9FAB6E82D3355A1B14A1CD363A51FC74DC0B6FC")throw new BackupException("Core 完整结构指纹未受支持（包括列、索引和触发器），请使用隔离恢复。");
        var migrationRows=new List<string>();
        using(var c=db.CreateCommand())
        {
            c.CommandText="SELECT version,hex(checksum),success FROM _sqlx_migrations ORDER BY version LIMIT 1001";
            using var r=await c.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))migrationRows.Add(r.GetInt64(0).ToString(CultureInfo.InvariantCulture)+":"+r.GetString(1)+":"+r.GetInt64(2).ToString(CultureInfo.InvariantCulture));
        }
        if(Digest(migrationRows)!="33AA1A745D046E0D6316F03A76DA60417462FE95C6396E945EDD95E7A69EFADE")throw new BackupException("Core 迁移版本或校验元数据未受支持，请使用隔离恢复。");
        using(var c=db.CreateCommand()) {c.CommandText="PRAGMA quick_check"; if(!Equals(await c.ExecuteScalarAsync(ct),"ok")) throw new BackupException("Core 数据库完整性检查失败。");}
        var tables=new HashSet<string>(StringComparer.Ordinal);
        using(var c=db.CreateCommand())
        {
            c.CommandText="SELECT type,name FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
            using var r=await c.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var type=r.GetString(0);var name=r.GetString(1);
                if(type is "view" || (type=="table" && !Tables.Contains(name))) throw new BackupException($"不支持的 Core 数据库结构：{type} {name}；拒绝自动恢复。");
                if(type=="table") tables.Add(name);
            }
        }
        if(!tables.Contains("threads")) throw new BackupException("Core 数据库缺少已知 threads 结构。");
        async Task<Dictionary<string,string>> Columns(string table)
        {
            using var c=db.CreateCommand();c.CommandText=$"PRAGMA table_info(\"{table}\")";
            using var r=await c.ExecuteReaderAsync(ct); var result=new Dictionary<string,string>();
            while(await r.ReadAsync(ct)) result[r.GetString(1)]=r.GetString(2);
            return result;
        }
        var threadColumns=await Columns("threads");
        foreach(var required in new[]{"cwd","rollout_path"})
            if(!threadColumns.TryGetValue(required,out var type) || !type.Equals("TEXT",StringComparison.OrdinalIgnoreCase)) throw new BackupException("Core threads 路径结构不受支持。");
        // Updates use rowid and bound values, never run source SQL or replace arbitrary text.
        foreach(var table in new[]{"threads","project_roots"}.Where(tables.Contains))
        {
            var columns=await Columns(table);
            if(table=="project_roots" && !columns.ContainsKey("path") && !columns.ContainsKey("root_path")) throw new BackupException("Core project_roots 路径结构不受支持。");
            foreach(var column in (table=="threads"?new[]{"cwd","rollout_path"}:new[]{"path","root_path"}).Where(columns.ContainsKey))
            {
                if(!columns[column].Equals("TEXT",StringComparison.OrdinalIgnoreCase)) throw new BackupException("Core 路径列类型不受支持。");
                var rows=new List<(long,string)>();
                using(var c=db.CreateCommand()) {c.CommandText=$"SELECT rowid,\"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL LIMIT 100001";using var r=await c.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) {var value=r.GetString(1);if(rows.Count>=100000 || value.Length>32768)throw new BackupException("Core 路径记录超出安全适配上限。");rows.Add((r.GetInt64(0),value));}}
                foreach(var (id,value) in rows) {var mapped=map(value);if(mapped==value)continue; using var c=db.CreateCommand();c.CommandText=$"UPDATE \"{table}\" SET \"{column}\"=$value WHERE rowid=$id";c.Parameters.AddWithValue("$value",mapped);c.Parameters.AddWithValue("$id",id);await c.ExecuteNonQueryAsync(ct);}
            }
        }
        foreach(var table in new[]{"thread_dynamic_tools","remote_control_enrollments","external_agent_config_imports"}.Where(tables.Contains)) { await Run($"DELETE FROM \"{table}\""); notes.Add($"已清除活动集成记录：{table}（原始备份不变）"); }
        if(threadColumns.ContainsKey("daybreak_enabled")) await Run("UPDATE threads SET daybreak_enabled=0");
        if(threadColumns.ContainsKey("agent_path")) notes.Add("agent_path 为代理身份字段，未按文件路径改写。");
        await Run("PRAGMA wal_checkpoint(TRUNCATE)");
    }
}
