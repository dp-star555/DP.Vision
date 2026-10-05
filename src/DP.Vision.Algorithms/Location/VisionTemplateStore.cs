using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>模型文件清单。</summary>
[DataContract]
public sealed class VisionTemplateFile
{
    /// <summary>包内相对路径。</summary>
    [DataMember] public string Path { get; set; } = "";
    /// <summary>内容SHA256。</summary>
    [DataMember] public string Hash { get; set; } = "";
    /// <summary>字节数。</summary>
    [DataMember] public int Length { get; set; }
}

/// <summary>统一模板清单；引擎私有文件采用不透明格式。</summary>
[DataContract]
public sealed class VisionTemplateManifest
{
    /// <summary>共同格式版本。</summary>
    [DataMember] public int SchemaVersion { get; set; } = 1;
    /// <summary>模板身份。</summary>
    [DataMember] public string TemplateId { get; set; } = "";
    /// <summary>用户可读名称；旧资源缺少此项时仍使用原有身份。</summary>
    [DataMember(EmitDefaultValue = false)] public string? DisplayName { get; set; }
    /// <summary>不可变修订。</summary>
    [DataMember] public string RevisionId { get; set; } = "";
    /// <summary>配套匹配实现。</summary>
    [DataMember] public string ImplementationId { get; set; } = "";
    /// <summary>引擎文件格式签名。</summary>
    [DataMember] public string ModelFormat { get; set; } = "";
    /// <summary>共同参考定义。</summary>
    [DataMember] public VisionTemplateDefinition Definition { get; set; } = new VisionTemplateDefinition();
    /// <summary>制作配置。</summary>
    [DataMember] public Dictionary<string, string> BuildSettings { get; set; } = new Dictionary<string, string>();
    /// <summary>全部文件及内容身份。</summary>
    [DataMember] public List<VisionTemplateFile> Files { get; set; } = new List<VisionTemplateFile>();
}

/// <summary>通过完整校验捕获的资源快照；之后修改磁盘文件不影响本次准备。</summary>
public sealed class VisionTemplateSnapshot
{
    internal VisionTemplateSnapshot(VisionTemplateManifest manifest, Dictionary<string, byte[]> files, string identity)
    { Manifest = manifest; _files = files; Identity = identity; }
    private readonly Dictionary<string, byte[]> _files;
    /// <summary>清单快照。</summary>
    public VisionTemplateManifest Manifest { get; }
    /// <summary>清单与全部内容的身份。</summary>
    public string Identity { get; }
    /// <summary>返回调用者拥有的文件内容。</summary>
    public byte[] Read(string name) => _files.TryGetValue(name, out var bytes) ? (byte[])bytes.Clone() : throw new InvalidDataException("模板缺少文件：" + name);
}

/// <summary>不可变资源发布和读取；不识别厂商文件格式，不覆盖旧版本。</summary>
public static class VisionTemplateStore
{
    private const int Limit = 64 * 1024 * 1024;
    /// <summary>计算内容SHA256。</summary>
    public static string Hash(byte[] bytes)
    { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }

    /// <summary>捕获制作结果供引擎试匹配，不写入磁盘。</summary>
    public static VisionTemplateSnapshot CaptureBuild(VisionTemplateBuild build)
    {
        build.Definition.Validate();
        var manifest = new VisionTemplateManifest { ImplementationId = build.ImplementationId, ModelFormat = build.Format,
            Definition = CopyDefinition(build.Definition), BuildSettings = build.Settings.ToDictionary(p => p.Key, p => p.Value) };
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in build.Files)
        { var bytes = (byte[])item.Content.Clone(); files.Add(item.Name, bytes); manifest.Files.Add(new VisionTemplateFile { Path = item.Name, Hash = Hash(bytes), Length = bytes.Length }); }
        return new VisionTemplateSnapshot(manifest, files, ContentIdentity(manifest));
    }

    /// <summary>深复制参考几何，防止界面草稿修改运行资源。</summary>
    public static VisionTemplateDefinition CopyDefinition(VisionTemplateDefinition d) => new VisionTemplateDefinition
    { SourceWidth = d.SourceWidth, SourceHeight = d.SourceHeight, X = d.X, Y = d.Y, Width = d.Width, Height = d.Height,
      OriginX = d.OriginX, OriginY = d.OriginY, AxisAngleRadians = d.AxisAngleRadians, ReferenceVersion = d.ReferenceVersion, ReferenceIdentity = d.ReferenceIdentity };

    /// <summary>发布新版本，返回相对于配方目录的清单路径；取消不会更新节点引用。</summary>
    public static string Publish(string recipeDirectory, string templateId, VisionTemplateBuild build, CancellationToken token = default)
        => Publish(recipeDirectory, templateId, build, null, token);

    /// <summary>按可读名称发布新版本；名称不改变资源身份或原生模型内容身份。</summary>
    public static string Publish(string recipeDirectory, string templateId, VisionTemplateBuild build, string? displayName, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(recipeDirectory) || !Path.IsPathRooted(recipeDirectory)) throw new ArgumentException("请先保存配方，确定资源目录。");
        if (!Guid.TryParseExact(templateId, "N", out _)) throw new ArgumentException("模板身份必须是稳定GUID。");
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName!.Trim();
        if (displayName != null && (displayName.Length > 100 || displayName.Any(char.IsControl))) throw new ArgumentException("模板名称最多100个字符，不能包含控制字符。", nameof(displayName));
        token.ThrowIfCancellationRequested();
        build.Definition.Validate();
        if (string.IsNullOrWhiteSpace(build.ImplementationId) || string.IsNullOrWhiteSpace(build.Format)) throw new ArgumentException("模板实现和格式不能为空。");
        var revision = Guid.NewGuid().ToString("N");
        var relative = Path.Combine("Resources", "Templates", templateId, "revisions", revision);
        var parent = Path.GetFullPath(Path.Combine(recipeDirectory, "Resources", "Templates", templateId, "revisions"));
        Directory.CreateDirectory(parent); CheckDirectory(parent);
        var stage = Path.Combine(parent, ".draft-" + revision);
        var destination = Path.Combine(recipeDirectory, relative);
        var manifest = new VisionTemplateManifest { TemplateId = templateId, RevisionId = revision, DisplayName = displayName,
            ImplementationId = build.ImplementationId, ModelFormat = build.Format, Definition = CopyDefinition(build.Definition),
            BuildSettings = build.Settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
        if (string.IsNullOrWhiteSpace(manifest.Definition.ReferenceIdentity)) manifest.Definition.ReferenceIdentity = templateId;
        var captured = build.Files.Select(f => new VisionTemplateArtifact(f.Name, f.Content)).ToArray();
        if (captured.Length == 0 || captured.Length > 64 || captured.Sum(f => (long)f.Content.Length) > 2L * Limit) throw new ArgumentException("模板文件数量或内容超过预算。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in captured)
        {
            _ = Within(parent, file.Name);
            if (file.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || !names.Add(file.Name.Replace('\\', '/'))
                || file.Content.Length == 0 || file.Content.Length > Limit) throw new ArgumentException("模板文件名重复、为空或内容超过预算。");
            manifest.Files.Add(new VisionTemplateFile { Path = file.Name.Replace('\\', '/'), Hash = Hash(file.Content), Length = file.Content.Length });
        }
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in captured)
            {
                token.ThrowIfCancellationRequested(); var path = Within(stage, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, file.Content);
            }
            using (var stream = File.Create(Path.Combine(stage, "manifest.json"))) Serializer().WriteObject(stream, manifest);
            token.ThrowIfCancellationRequested(); Directory.Move(stage, destination);
            return Path.Combine(relative, "manifest.json");
        }
        finally
        {
            // stage是本次创建、验证在parent内的确定目录，不清理已发布版本。
            if (Directory.Exists(stage)) { _ = Within(parent, Path.GetFileName(stage)); CheckDirectory(stage); Directory.Delete(stage, true); }
        }
    }

    /// <summary>只读清单，供编辑和静态检查；不加载引擎模型。</summary>
    public static VisionTemplateManifest Inspect(string path)
    {
        CheckDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.OpenRead(path);
        if (file.Length == 0 || file.Length > 1024 * 1024) throw new InvalidDataException("模板清单大小无效。");
        var manifest = Serializer().ReadObject(file) as VisionTemplateManifest ?? throw new InvalidDataException("模板清单为空。");
        if (manifest.SchemaVersion != 1 || !Guid.TryParseExact(manifest.TemplateId, "N", out _) || !Guid.TryParseExact(manifest.RevisionId, "N", out _)
            || string.IsNullOrWhiteSpace(manifest.ImplementationId) || string.IsNullOrWhiteSpace(manifest.ModelFormat)
            || manifest.Definition == null || manifest.Files == null || manifest.BuildSettings == null)
            throw new InvalidDataException("不支持的模板清单或身份缺失。");
        manifest.Definition.Validate();
        if (manifest.DisplayName != null && (manifest.DisplayName.Length > 100 || manifest.DisplayName.Any(char.IsControl))) throw new InvalidDataException("模板名称无效。");
        if (manifest.Files.Count == 0 || manifest.Files.Count > 64 || manifest.Files.Sum(f => (long)f.Length) > 2L * Limit)
            throw new InvalidDataException("模板清单超过资源预算。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            _ = Within(Path.GetDirectoryName(Path.GetFullPath(path))!, item.Path);
            if (!names.Add(item.Path.Replace('\\', '/')) || item.Length < 1 || item.Length > Limit || item.Hash == null || item.Hash.Length != 64)
                throw new InvalidDataException("模板文件清单无效。");
        }
        return manifest;
    }

    /// <summary>校验所有文件并捕获快照；异常发生在运行准备而非第一次匹配。</summary>
    public static VisionTemplateSnapshot Capture(string path, CancellationToken token = default)
    {
        var manifest = Inspect(path); var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in manifest.Files)
        {
            token.ThrowIfCancellationRequested(); var full = Within(Path.GetDirectoryName(Path.GetFullPath(path))!, item.Path);
            CheckDirectory(Path.GetDirectoryName(full)!);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模板文件不能是链接。");
            using var file = File.OpenRead(full);
            if (file.Length != item.Length) throw new InvalidDataException("模板文件大小已改变：" + item.Path);
            var bytes = new byte[item.Length]; int position = 0;
            while (position < bytes.Length) { token.ThrowIfCancellationRequested(); int read = file.Read(bytes, position, Math.Min(81920, bytes.Length - position)); if (read == 0) throw new EndOfStreamException(); position += read; }
            if (Hash(bytes) != item.Hash) throw new InvalidDataException("模板文件校验失败：" + item.Path);
            files.Add(item.Path, bytes);
        }
        return new VisionTemplateSnapshot(manifest, files, ContentIdentity(manifest));
    }

    /// <summary>仅检查包内文件存在性和长度，不读取模型内容。</summary>
    public static IReadOnlyList<string> InspectFiles(string path)
    {
        var manifest = Inspect(path); var errors = new List<string>();
        foreach (var item in manifest.Files)
        {
            var full = Within(Path.GetDirectoryName(Path.GetFullPath(path))!, item.Path);
            if (!File.Exists(full)) errors.Add("模板文件不存在：" + item.Path);
            else if (new FileInfo(full).Length != item.Length) errors.Add("模板文件大小不一致：" + item.Path);
        }
        return errors;
    }

    private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(VisionTemplateManifest));
    private static string ContentIdentity(VisionTemplateManifest manifest)
    {
        // 修订只是部署引用；同内容、同参考定义和同制作配置可共享模型。
        var content = new VisionTemplateManifest { ImplementationId = manifest.ImplementationId, ModelFormat = manifest.ModelFormat,
            Definition = CopyDefinition(manifest.Definition), BuildSettings = manifest.BuildSettings.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
            Files = manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList() };
        using var canonical = new MemoryStream(); Serializer().WriteObject(canonical, content); return Hash(canonical.ToArray());
    }
    private static string Within(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0
            || relative.Split('/', '\\').Any(p => p == ".." || p == "." || p.Length == 0 || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("模板文件必须是包内相对路径。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模板文件超出包目录。");
        return result;
    }
    private static void CheckDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模板资源目录不能经过链接。");
    }
}
