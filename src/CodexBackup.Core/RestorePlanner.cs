using System.Text;
using System.Text.Json.Nodes;

namespace CodexBackup.Core;

public static class RestorePlanner
{
    internal static string Normalize(string path)
    {
        if(path.StartsWith("\\\\?\\",StringComparison.Ordinal) && path.Length>6 && char.IsAsciiLetter(path[4]) && path[5]==':') path=path[4..];
        return PathSafety.Full(path);
    }
    public static List<RestoreMapping> CreateMappings(BackupManifest manifest,string? newBase,string runtimeCoreHome,bool originalLayout)
    {
        if(!originalLayout && string.IsNullOrWhiteSpace(newBase)) throw new BackupException("请选择新的恢复根目录。");
        var result=new List<RestoreMapping>();
        var primaryCore = manifest.Roots.FirstOrDefault(r => r.Kind == SourceKind.Core && Normalize(r.OriginalPath).Equals(Normalize(Path.Combine(manifest.SourceProfile,".codex")),StringComparison.OrdinalIgnoreCase))
            ?? manifest.Roots.FirstOrDefault(r => r.Kind == SourceKind.Core);
        foreach(var root in manifest.Roots)
        {
            var old=Normalize(root.OriginalPath);
            var isolate=root.Kind is SourceKind.Application or SourceKind.Tool;
            string target;
            if(root.Kind==SourceKind.Core && root.Id==primaryCore?.Id) target=Normalize(runtimeCoreHome);
            else if(root.Kind==SourceKind.Core) target=Path.Combine(Normalize(newBase??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Codex-Restored")),"additional-codex-homes",root.Id);
            else if(originalLayout && !isolate) target=old;
            else
            {
                var basis=Normalize(newBase??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Codex-Restored"));
                var drive=Path.GetPathRoot(old)!;
                if(drive.Length!=3 || drive[1]!=':')throw new BackupException("恢复布局仅支持本地盘符路径。");
                target=Path.Combine(basis,isolate?"disabled-applications":"drives",drive[..1],old[drive.Length..]);
            }
            result.Add(new(){RootId=root.Id,TargetPath=target});
        }
        return result;
    }
    internal static string Map(string value,IReadOnlyDictionary<string,string> mappings)
    {
        var normalized=Normalize(value);
        foreach(var pair in mappings.OrderByDescending(p=>p.Key.Length))
            if(PathSafety.Contains(pair.Key,normalized)) return Normalize(pair.Value+normalized[pair.Key.TrimEnd('\\').Length..]);
        return normalized;
    }
    internal static Dictionary<string,string> BuildPathMappings(BackupManifest manifest,IReadOnlyList<RestoreMapping> mappings)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var mapping in mappings) result.Add(Normalize(manifest.Roots.Single(r=>r.Id==mapping.RootId).OriginalPath),Normalize(mapping.TargetPath));
        foreach(var alias in manifest.PathReplacements)
        {
            var actual=Normalize(alias.Value);
            if(!result.Keys.Any(root=>PathSafety.Contains(root,actual)))throw new BackupException("旧路径替代位置没有包含在恢复映射中。");
            var old=Normalize(alias.Key);var target=Map(actual,result);
            if(result.TryGetValue(old,out var existing) && !existing.Equals(target,StringComparison.OrdinalIgnoreCase))throw new BackupException("旧路径别名与恢复映射冲突。");
            result[old]=target;
        }
        return result;
    }
    internal static (BackupRoot Root,FileRecord Record) Resolve(VerifiedPackage package,string oldPath,bool directory)
    {
        var aliases=package.Manifest.PathReplacements.ToDictionary(p=>Normalize(p.Key),p=>Normalize(p.Value),StringComparer.OrdinalIgnoreCase);
        var actual=Map(oldPath,aliases);
        foreach(var root in package.Manifest.Roots.OrderByDescending(r=>r.OriginalPath.Length))
        {
            var rootPath=Normalize(root.OriginalPath);
            if(!PathSafety.Contains(rootPath,actual))continue;
            var relative=actual.Equals(rootPath,StringComparison.OrdinalIgnoreCase)?"":Path.GetRelativePath(rootPath,actual);
            var record=package.Files.SingleOrDefault(f=>f.RootId==root.Id && f.RelativePath.Replace('/','\\').Equals(relative,StringComparison.OrdinalIgnoreCase) && f.IsDirectory==directory);
            if(record is not null)return(root,record);
        }
        throw new BackupException(directory?"会话关联的项目目录未完整包含在备份清单中。":"会话正文文件未包含在备份清单中。");
    }
    internal static void ValidateCoverage(VerifiedPackage package,RestoreRequest request,IReadOnlyDictionary<string,string>? physicalRoots=null,List<string>? notes=null)
    {
        if(!request.RequireCompleteMigration)return;
        if(request.Isolated)throw new BackupException("完整迁移必须启用路径适配；隔离模式只能恢复原始文件副本。");
        if(!package.Manifest.CompleteMigration || package.Manifest.LogicalSources.Count==0)throw new BackupException("此备份未记录完整迁移覆盖信息，请重新创建完整备份或明确使用部分恢复。");
        if(request.Mappings.Count!=package.Manifest.Roots.Count || package.Manifest.Roots.Any(r=>!request.Mappings.Any(m=>m.RootId==r.Id)))throw new BackupException("完整迁移必须恢复全部根目录。");
        var pathMappings=BuildPathMappings(package.Manifest,request.Mappings);
        MigrationCoverage.ValidateInventory(package.Manifest,package.Files);
        foreach(var source in package.Manifest.LogicalSources.Where(s=>!(s.Kind==SourceKind.Core && !s.Exists && !s.Required) && (s.Kind is SourceKind.Project or SourceKind.Core or SourceKind.Session or SourceKind.Memory || s.Required)))
        {
            var segments=Normalize(source.Path).Split('\\');
            var git=segments.Any(p=>p.Equals(".git",StringComparison.OrdinalIgnoreCase)) || segments.Last() is "gitdir" or "commondir";
            var strict=source.Kind is SourceKind.Project or SourceKind.Session or SourceKind.Memory or SourceKind.Core || source.Path.Contains("project",StringComparison.OrdinalIgnoreCase) || git;
            var integration=source.Kind is SourceKind.Skill or SourceKind.Plugin or SourceKind.Tool or SourceKind.Application or SourceKind.Environment;
            var knownConfig=!source.IsDirectory && segments.Last() is "config.toml" or "auth.json";
            Check(source.Path,source.IsDirectory,!strict && (integration || knownConfig));
        }
        foreach(var session in package.Manifest.Sessions) {Check(session.ProjectPath,true);Check(session.TranscriptPath,false);}
        void Check(string path,bool directory,bool allowDisabled=false)
        {
            var resolved=Resolve(package,path,directory);
            var mappedRoot=Normalize(request.Mappings.Single(m=>m.RootId==resolved.Root.Id).TargetPath);
            var expected=resolved.Record.RelativePath.Length==0?mappedRoot:PathSafety.Under(mappedRoot,resolved.Record.RelativePath);
            if(!Map(path,pathMappings).Equals(Normalize(expected),StringComparison.OrdinalIgnoreCase))throw new BackupException("会话或来源路径映射与实际文件位置不一致，已阻止完整迁移。");
            if(physicalRoots is null)return;
            if(!physicalRoots.TryGetValue(resolved.Root.Id,out var physical))throw new BackupException("恢复缺少会话依赖根目录。");
            var target=resolved.Record.RelativePath.Length==0?physical:PathSafety.Under(physical,resolved.Record.RelativePath);
            PathSafety.RejectReparseAncestors(target);
            if(directory?Directory.Exists(target):File.Exists(target))return;
            if(allowDisabled && resolved.Root.Kind==SourceKind.Core && resolved.Record.RelativePath.Length>0)
            {
                var disabled=PathSafety.Under(physical,".codex-backup-disabled\\"+resolved.Record.RelativePath);
                PathSafety.RejectReparseAncestors(disabled);
                if(directory?Directory.Exists(disabled):File.Exists(disabled))
                {
                    notes?.Add($"集成资源已完整保存但保持停用：{path}。请重新登录并审核后手动启用；此检查不表示集成已经可运行。");
                    return;
                }
            }
            throw new BackupException("恢复后的会话正文、项目目录或关联来源缺失，不能报告完整迁移。");
        }
    }
    internal static async Task PrepareStructuralFilesAsync(string stage,BackupRoot root,IReadOnlyList<FileRecord> files,VerifiedPackage package,IReadOnlyDictionary<string,string> mappings,CancellationToken ct)
    {
        foreach(var record in files.Where(f=>!f.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var relative=record.RelativePath.Replace('/','\\');
            var oldFile=relative.Length==0?Normalize(root.OriginalPath):Path.Combine(Normalize(root.OriginalPath),relative);
            var name=Path.GetFileName(oldFile);
            var isGit=name==".git" || ((name=="commondir" || name=="gitdir") && oldFile.Contains("\\.git\\",StringComparison.OrdinalIgnoreCase));
            if(!isGit)continue;
            var path=relative.Length==0?stage:PathSafety.Under(stage,relative);
            if(!File.Exists(path))continue;
            PathSafety.RejectReparseAncestors(path);
            if(new FileInfo(path).Length>32768)throw new BackupException("Git 关联文件超过安全上限。");
            var content=await File.ReadAllTextAsync(path,Encoding.UTF8,ct);var value=content.Trim();
            if(name==".git") {if(!value.StartsWith("gitdir:",StringComparison.Ordinal))throw new BackupException("Git 工作树指针格式不受支持。");value=value[7..].Trim();if(value.Length==0)throw new BackupException("Git 工作树指针为空。");}
            var oldTarget=Path.IsPathFullyQualified(value)?Normalize(value):Normalize(Path.Combine(Path.GetDirectoryName(oldFile)!,value));
            if(!mappings.Keys.Any(p=>PathSafety.Contains(p,oldTarget)))throw new BackupException("Git 工作树引用位于备份映射之外，请先补齐关联仓库。");
            var mapped=Map(oldTarget,mappings);
            await File.WriteAllTextAsync(path,(name==".git"?"gitdir: ":"")+mapped+Environment.NewLine,new UTF8Encoding(false,true),ct);
        }
        foreach(var session in package.Manifest.Sessions)
        {
            if(!MigrationCoverage.ContainsPath(package.Manifest,package.Files,session.TranscriptPath,false))continue;
            var resolved=Resolve(package,session.TranscriptPath,false);
            if(resolved.Root.Id!=root.Id)continue;
            var file=resolved.Record.RelativePath.Length==0?stage:PathSafety.Under(stage,resolved.Record.RelativePath);
            await RewriteSessionMetaAsync(file,mappings,ct);
        }
    }
    internal static async Task RewriteSessionMetaAsync(string path,IReadOnlyDictionary<string,string> mappings,CancellationToken ct)
    {
        PathSafety.RejectReparseAncestors(path);
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var first=new List<byte>();int b;
        while((b=input.ReadByte())>=0 && b!=10) {if(first.Count>=1024*1024)throw new BackupException("会话首行超过安全解析上限。");first.Add((byte)b);}
        var text=new UTF8Encoding(false,true).GetString(first.ToArray()).TrimStart('\uFEFF');
        JsonObject? node;
        try {node=JsonNode.Parse(text) as JsonObject;} catch(System.Text.Json.JsonException){throw new BackupException("会话元数据首行无法解析。");}
        if(node?["type"]?.GetValue<string>()!="session_meta" || node["payload"] is not JsonObject payload || payload["cwd"] is not JsonValue cwd || !cwd.TryGetValue<string>(out var old))return;
        var mapped=Map(old,mappings);if(mapped==old)return;
        payload["cwd"]=mapped;
        var temp=path+".relocate-"+Guid.NewGuid().ToString("N");
        try
        {
            await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(node.ToJsonString()+(b==10?"\n":"")),ct);
                await input.CopyToAsync(output,ct);
            }
            await input.DisposeAsync();File.Move(temp,path,true);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
