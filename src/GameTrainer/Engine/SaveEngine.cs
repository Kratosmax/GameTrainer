using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTrainer.Core;
using GameTrainer.Profiles;

namespace GameTrainer.Engine;

internal sealed class SnapshotInfo
{
    public string Id { get; set; }
    public string ProfileId { get; set; }
    public string SourcePath { get; set; }
    /// <summary>源路径是目录还是单文件。恢复时据此决定落盘方式，不靠猜。</summary>
    public bool SourceIsDirectory { get; set; }
    public DateTime At { get; set; }
    public List<SnapshotFile> Files { get; set; } = new();
    public string Note { get; set; }
}

internal sealed class SnapshotFile
{
    public string RelativePath { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; }
}

/// <summary>
/// 存档后端。
///
/// 铁律：任何修改动作之前必须先成功落一份快照。
/// 快照带 SHA256，恢复时校验，用来证明"恢复的确实是当初那份"。
///
/// IndexedDB / LevelDB 这类二进制存储不解析、不编辑，只做整目录快照与整体恢复。
/// </summary>
internal sealed class SaveEngine
{
    private readonly GameProfile _profile;
    private readonly AuditLog _log;
    private readonly string _snapshotRoot;
    private readonly ActiveStateStore _activeState;

    public SaveEngine(GameProfile profile, AuditLog log, string skillRoot, ActiveStateStore activeState = null)
    {
        _profile = profile;
        _log = log;
        _activeState = activeState;
        _snapshotRoot = Path.Combine(skillRoot, "snapshots", profile.Id);
        Directory.CreateDirectory(_snapshotRoot);
    }

    private string ResolveSavePath()
    {
        string raw = _profile.Save?.Path;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("profile 未配置 save.path。");
        return Environment.ExpandEnvironmentVariables(raw);
    }

    /// <summary>创建快照。返回快照 ID。</summary>
    public SnapshotInfo CreateSnapshot(string note = null)
    {
        string src = ResolveSavePath();
        if (!File.Exists(src) && !Directory.Exists(src))
            throw new InvalidOperationException($"存档路径不存在：{src}");

        string id = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string dest = Path.Combine(_snapshotRoot, id);
        Directory.CreateDirectory(dest);

        var info = new SnapshotInfo
        {
            Id = id,
            ProfileId = _profile.Id,
            SourcePath = src,
            SourceIsDirectory = Directory.Exists(src),
            At = DateTime.Now,
            Note = note
        };

        if (File.Exists(src))
        {
            string name = Path.GetFileName(src);
            File.Copy(src, Path.Combine(dest, name), overwrite: true);
            info.Files.Add(MakeEntry(name, Path.Combine(dest, name)));
        }
        else
        {
            foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, f);
                string target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: true);
                info.Files.Add(MakeEntry(rel, target));
            }
        }

        File.WriteAllText(Path.Combine(dest, "snapshot.json"),
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        _log.Record("snapshot", new
        {
            id,
            source = src,
            files = info.Files.Count,
            bytes = info.Files.Sum(f => f.Size)
        });

        return info;
    }

    private static SnapshotFile MakeEntry(string rel, string path)
    {
        var fi = new FileInfo(path);
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return new SnapshotFile
        {
            RelativePath = rel,
            Size = fi.Length,
            Sha256 = Convert.ToHexString(sha.ComputeHash(fs))
        };
    }

    public List<SnapshotInfo> ListSnapshots()
    {
        var list = new List<SnapshotInfo>();
        foreach (string dir in Directory.EnumerateDirectories(_snapshotRoot))
        {
            string meta = Path.Combine(dir, "snapshot.json");
            if (!File.Exists(meta)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllText(meta));
                if (info != null) list.Add(info);
            }
            catch { }
        }
        return list.OrderByDescending(s => s.At).ToList();
    }

    /// <summary>
    /// 从快照恢复。恢复前会先把"当前状态"再存一份（防止恢复本身造成不可逆损失）。
    /// 恢复时逐文件校验 SHA256，全部匹配才落盘。
    /// </summary>
    public ApplyResult Restore(string snapshotId)
    {
        string dir = Path.Combine(_snapshotRoot, snapshotId);
        string meta = Path.Combine(dir, "snapshot.json");
        if (!File.Exists(meta)) return ApplyResult.Fail($"快照 {snapshotId} 不存在。");

        var info = JsonSerializer.Deserialize<SnapshotInfo>(File.ReadAllText(meta));
        if (info == null) return ApplyResult.Fail($"快照 {snapshotId} 元数据损坏。");

        string dest = ResolveSavePath();

        // 恢复前先备份当前状态
        SnapshotInfo pre = null;
        try
        {
            pre = CreateSnapshot($"pre-restore of {snapshotId}");
        }
        catch (Exception ex)
        {
            _log.Record("restore_prebackup_failed", new { error = ex.Message });
            return ApplyResult.Fail(
                $"恢复前无法备份当前存档，已中止（避免不可逆丢失）：{ex.Message}");
        }

        // 先校验全部文件
        var errors = new List<string>();
        foreach (var f in info.Files)
        {
            string p = Path.Combine(dir, f.RelativePath);
            if (!File.Exists(p)) { errors.Add($"快照内缺少文件: {f.RelativePath}"); continue; }
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(p);
            string actual = Convert.ToHexString(sha.ComputeHash(fs));
            if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add($"SHA256 不匹配: {f.RelativePath}");
        }

        if (errors.Count > 0)
            return ApplyResult.Fail($"快照校验失败，已中止恢复。当前状态已备份为 {pre.Id}。", errors.ToArray());

        // 校验通过才落盘。单文件快照恢复回原路径，目录快照按相对路径还原。
        foreach (var f in info.Files)
        {
            string srcFile = Path.Combine(dir, f.RelativePath);
            string dstFile = info.SourceIsDirectory
                ? Path.Combine(dest, f.RelativePath)
                : dest;
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(srcFile, dstFile, overwrite: true);
        }

        _log.Record("restore", new { snapshot = snapshotId, dest, files = info.Files.Count, preBackup = pre.Id });
        return ApplyResult.Success(
            $"已从快照 {snapshotId} 恢复 {info.Files.Count} 个文件到 {dest}。"
            + $"（恢复前状态已备份为 {pre.Id}）");
    }

    /// <summary>
    /// 执行存档编辑。仅支持单文件、已知压缩格式的文本存档。
    /// 二进制存储（IndexedDB/LevelDB）一律拒绝编辑，只允许快照/恢复。
    /// </summary>
    public ApplyResult ApplyEdit(SaveWriteDef def)
    {
        string mode = _profile.Save?.EditMode ?? "backup_only";
        if (mode == "backup_only")
            return ApplyResult.Fail("profile 的 save.editMode = backup_only，禁止编辑存档内容。");

        string root = ResolveSavePath();
        string file = string.IsNullOrWhiteSpace(def.File) ? root : Path.Combine(root, def.File);
        if (!File.Exists(file)) return ApplyResult.Fail($"存档文件不存在：{file}");

        // 拒绝已知二进制存储
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".log" or ".ldb" or ".sst" or ".db")
            return ApplyResult.Fail($"拒绝编辑二进制存储文件（{ext}）：这类格式无可靠回写保证，请只用快照/恢复。");

        SnapshotInfo snap = CreateSnapshot($"pre-edit {Path.GetFileName(file)}");

        byte[] original;
        try { original = File.ReadAllBytes(file); }
        catch (Exception ex)
        {
            return ApplyResult.Fail($"读取存档失败（游戏可能正占用该文件，请先退出游戏）：{ex.Message}");
        }

        byte[] plaintext;
        try
        {
            plaintext = Decompress(original, def.Compression);
        }
        catch (Exception ex)
        {
            return ApplyResult.Fail($"解压失败，已做快照 {snap.Id} 但未改动文件：{ex.Message}");
        }

        string text = Encoding.UTF8.GetString(plaintext);
        string edited = text;
        var applied = new List<string>();

        foreach (SaveEdit e in def.Edits)
        {
            if (string.IsNullOrWhiteSpace(e.Pattern)) continue;
            var rx = new Regex(e.Pattern);
            int n = rx.Matches(edited).Count;
            if (n == 0)
            {
                return ApplyResult.Fail(
                    $"正则未命中，已做快照 {snap.Id} 但未改动文件：{e.Pattern}",
                    applied.ToArray());
            }
            edited = rx.Replace(edited, e.Replacement);
            applied.Add($"{e.Pattern} → {e.Replacement}（{n} 处）");
        }

        if (edited == text)
            return ApplyResult.Fail("没有任何替换发生，文件未改动。", applied.ToArray());

        byte[] editedBytes;
        try
        {
            editedBytes = Compress(Encoding.UTF8.GetBytes(edited), def.Compression);
        }
        catch (Exception ex)
        {
            return ApplyResult.Fail($"重新压缩失败，已中止且未改动文件：{ex.Message}", applied.ToArray());
        }

        // 写入：先写临时文件再替换，避免写一半损坏
        string tmp = file + ".gt_tmp";
        try
        {
            File.WriteAllBytes(tmp, editedBytes);
            File.Replace(tmp, file, null, ignoreMetadataErrors: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return ApplyResult.Fail(
                $"写回失败，原文件未被替换：{ex.Message}。可用快照 {snap.Id} 核对。", applied.ToArray());
        }

        _log.Record("save_edit", new
        {
            file,
            compression = def.Compression,
            before_bytes = original.Length,
            after_bytes = editedBytes.Length,
            edits = applied,
            snapshot = snap.Id
        });

        return ApplyResult.Success(
            $"存档已修改：{file}（{original.Length} → {editedBytes.Length} 字节）。快照 {snap.Id}",
            applied);
    }

    private static byte[] Decompress(byte[] data, string mode)
    {
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase)) return data;

        using var input = new MemoryStream(data);
        Stream ds = string.Equals(mode, "zlib", StringComparison.OrdinalIgnoreCase)
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using (ds)
        {
            using var output = new MemoryStream();
            ds.CopyTo(output);
            return output.ToArray();
        }
    }

    private static byte[] Compress(byte[] data, string mode)
    {
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase)) return data;

        using var output = new MemoryStream();
        Stream cs = string.Equals(mode, "zlib", StringComparison.OrdinalIgnoreCase)
            ? new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)
            : new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using (cs) cs.Write(data, 0, data.Length);
        return output.ToArray();
    }
}
