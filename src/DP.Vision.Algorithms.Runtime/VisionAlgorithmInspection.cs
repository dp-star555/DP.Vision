using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>结构化算法问题；位置由调用方映射为节点和子流程。</summary>
public sealed class VisionAlgorithmIssue
{
    /// <summary>保存阶段、位置、实现和完整技术原因。</summary>
    public VisionAlgorithmIssue(string code, string phase, string bindingKey, string implementationId, string dependencyPath, string message, string? detail = null)
    { Code = code; Phase = phase; BindingKey = bindingKey; ImplementationId = implementationId; DependencyPath = dependencyPath; Message = message; Detail = detail; }
    /// <summary>稳定错误代码。</summary>
    public string Code { get; }
    /// <summary>Inspection、Preparation 等阶段。</summary>
    public string Phase { get; }
    /// <summary>根算法绑定身份。</summary>
    public string BindingKey { get; }
    /// <summary>发生问题的实现。</summary>
    public string ImplementationId { get; }
    /// <summary>根选择下的依赖槽位路径。</summary>
    public string DependencyPath { get; }
    /// <summary>可显示说明。</summary>
    public string Message { get; }
    /// <summary>原异常等技术详情。</summary>
    public string? Detail { get; }
}

/// <summary>只读静态检查报告；通过不代表资源已初始化。</summary>
public sealed class VisionAlgorithmInspectionReport
{
    /// <summary>冻结检查问题。</summary>
    public VisionAlgorithmInspectionReport(IEnumerable<VisionAlgorithmIssue> issues) => Issues = Array.AsReadOnly(issues.ToArray());
    /// <summary>全部独立问题。</summary>
    public IReadOnlyList<VisionAlgorithmIssue> Issues { get; }
    /// <summary>静态检查是否通过。</summary>
    public bool Success => Issues.Count == 0;
}

/// <summary>明确的资源解析上下文；没有配方目录时禁止猜测相对路径。</summary>
public sealed class VisionAlgorithmResourceContext
{
    /// <summary>保存已保存配方目录及机器资源根目录；均可为空。</summary>
    public VisionAlgorithmResourceContext(string? recipeDirectory = null, string? resourceDirectory = null)
    {
        RecipeDirectory = string.IsNullOrWhiteSpace(recipeDirectory) ? null : Path.GetFullPath(recipeDirectory);
        ResourceDirectory = string.IsNullOrWhiteSpace(resourceDirectory) ? null : Path.GetFullPath(resourceDirectory);
    }
    /// <summary>普通相对路径的基础目录。</summary>
    public string? RecipeDirectory { get; }
    /// <summary>resource: 引用的基础目录。</summary>
    public string? ResourceDirectory { get; }
    /// <summary>解析文件引用，不修改原节点的保存值。</summary>
    public string Resolve(string reference)
    {
        if (reference.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
        {
            if (ResourceDirectory == null) throw new InvalidOperationException("机器资源根目录未配置。");
            var relative = reference.Substring("resource:".Length);
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ArgumentException("resource: 后必须是资源根目录内的相对文件路径。");
            var path = Path.GetFullPath(Path.Combine(ResourceDirectory, relative));
            var root = ResourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("资源引用超出了机器资源根目录。");
            return path;
        }
        if (Path.IsPathRooted(reference)) return Path.GetFullPath(reference);
        if (RecipeDirectory == null) throw new InvalidOperationException("相对模型路径需要已保存配方的目录；也可以使用绝对路径或 resource: 引用。");
        return Path.GetFullPath(Path.Combine(RecipeDirectory, reference));
    }
    internal VisionAlgorithmConfiguration Resolve(VisionAlgorithmDescriptor descriptor, VisionAlgorithmConfiguration configuration)
    {
        var values = configuration.Settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var parameter in descriptor.Parameters.Where(p => p.IsFilePath))
            if (values.TryGetValue(parameter.Id, out var value) && !string.IsNullOrWhiteSpace(value)) values[parameter.Id] = Resolve(value);
        return new VisionAlgorithmConfiguration(configuration.SettingsVersion, values);
    }
}

/// <summary>无模型初始化的算法选择检查和显式配置升级。</summary>
public sealed class VisionAlgorithmInspection
{
    private readonly VisionAlgorithmCatalog _catalog;
    /// <summary>使用冻结算法目录。</summary>
    public VisionAlgorithmInspection(VisionAlgorithmCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    /// <summary>捕获完整选择图，拒绝引用循环。</summary>
    public static VisionAlgorithmSelection Snapshot(VisionAlgorithmSelection selection) => Clone(selection, new HashSet<VisionAlgorithmSelection>());
    /// <summary>检查全部绑定；只访问描述、轻量配置规则和可选文件存在性。</summary>
    public VisionAlgorithmInspectionReport Analyze(IEnumerable<VisionAlgorithmRequest> requests, VisionAlgorithmResourceContext? resources = null, bool checkFiles = true)
    {
        if (requests == null) throw new ArgumentNullException(nameof(requests));
        var issues = new List<VisionAlgorithmIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (!seen.Add(request.BindingKey)) issues.Add(new VisionAlgorithmIssue("ALG_DUPLICATE_BINDING", "Inspection", request.BindingKey, request.Selection.ImplementationId, "", "算法绑定身份重复。"));
            Walk(request.ContractType, request.Selection, request.RequiredFeatures, "", new HashSet<VisionAlgorithmSelection>(), request, issues, resources, checkFiles, 0);
        }
        return new VisionAlgorithmInspectionReport(issues);
    }
    private void Walk(Type contract, VisionAlgorithmSelection selection, IReadOnlyList<string> features, string dependencyPath,
        HashSet<VisionAlgorithmSelection> objects, VisionAlgorithmRequest request,
        List<VisionAlgorithmIssue> issues, VisionAlgorithmResourceContext? resources, bool checkFiles, int depth)
    {
        void Error(string code, string message, Exception? error = null) => issues.Add(new VisionAlgorithmIssue(code, "Inspection", request.BindingKey,
            selection?.ImplementationId ?? "", dependencyPath, message, error?.ToString()));
        if (selection == null || selection.Settings == null || selection.Dependencies == null) { Error("ALG_INVALID_SELECTION", "选择、设置或依赖配置为空。"); return; }
        if (depth > 64 || !objects.Add(selection)) { Error("ALG_DEPENDENCY_CYCLE", "选择配置存在引用循环或超过依赖深度预算。"); return; }
        if (string.IsNullOrWhiteSpace(selection.ImplementationId)) { Error("ALG_NO_SELECTION", "未选择算法实现。"); objects.Remove(selection); return; }
        try
        {
            if (!_catalog.TryGet(selection.ImplementationId, out var descriptor)) { Error("ALG_IMPLEMENTATION_MISSING", "算法实现未安装或登记失败：" + selection.ImplementationId); return; }
            if (!contract.IsAssignableFrom(descriptor!.ContractType) && !descriptor.ContractType.IsAssignableFrom(contract))
                Error("ALG_CONTRACT_MISMATCH", "所选实现不提供节点需要的能力：" + contract.FullName);
            foreach (var feature in features.Where(f => !descriptor.Features.Contains(f, StringComparer.Ordinal))) Error("ALG_FEATURE_MISSING", "所选实现缺少特征：" + feature);
            VisionAlgorithmConfiguration configuration;
            try { configuration = new VisionAlgorithmConfiguration(selection.SettingsVersion, selection.Settings); }
            catch (Exception error) when (error is ArgumentException) { Error("ALG_CONFIGURATION_INVALID", error.Message, error); return; }
            if (descriptor.Factory is IVisionAlgorithmConfigurationValidator validator)
                foreach (var message in validator.ValidateConfiguration(configuration)) Error("ALG_CONFIGURATION_INVALID", message);
            foreach (var parameter in descriptor.Parameters)
            {
                var text = configuration.Settings.TryGetValue(parameter.Id, out var configured) ? configured : parameter.DefaultValue;
                if (text == null) continue;
                try
                {
                    if (parameter.ValueType == typeof(bool)) _ = bool.Parse(text);
                    else if (parameter.ValueType.IsEnum) { var value = Enum.Parse(parameter.ValueType, text); if (!Enum.IsDefined(parameter.ValueType, value)) throw new ArgumentException("枚举值未定义。"); }
                    else if (parameter.ValueType != typeof(string))
                    {
                        var value = Convert.ChangeType(text, parameter.ValueType, CultureInfo.InvariantCulture);
                        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                        if (double.IsNaN(number) || double.IsInfinity(number) || number < parameter.Minimum || number > parameter.Maximum) throw new ArgumentOutOfRangeException(parameter.Id, "参数超出允许范围。");
                    }
                    if (checkFiles && parameter.IsFilePath && !string.IsNullOrWhiteSpace(text))
                    {
                        var path = resources == null ? Path.GetFullPath(text) : resources.Resolve(text);
                        if (checkFiles && !File.Exists(path)) Error("ALG_RESOURCE_MISSING", "模型或资源文件不存在：" + path);
                    }
                }
                catch (Exception error) when (error is ArgumentException || error is FormatException || error is OverflowException || error is InvalidOperationException || error is NotSupportedException)
                { Error("ALG_PARAMETER_INVALID", parameter.DisplayName + "：" + error.Message, error); }
            }
            var dependencies = descriptor.Factory.GetDependencies(configuration);
            if (checkFiles && descriptor.Factory is IVisionAlgorithmResourceInspector resourceInspector)
            {
                try
                {
                    var resolved = resources == null ? configuration : resources.Resolve(descriptor, configuration);
                    foreach (var message in resourceInspector.InspectResources(resolved)) Error("ALG_RESOURCE_INVALID", message);
                }
                catch (Exception error) when (error is not OutOfMemoryException && error is not OperationCanceledException)
                { Error("ALG_RESOURCE_INVALID", error.Message, error); }
            }
            foreach (var duplicate in dependencies.GroupBy(d => d.Slot, StringComparer.Ordinal).Where(g => g.Count() > 1)) Error("ALG_DEPENDENCY_INVALID", "重复依赖槽位：" + duplicate.Key);
            foreach (var dependency in dependencies.GroupBy(d => d.Slot, StringComparer.Ordinal).Select(g => g.First()))
                if (!selection.Dependencies.TryGetValue(dependency.Slot, out var chosen)) Error("ALG_DEPENDENCY_MISSING", "缺少依赖选择：" + dependency.Slot);
                else Walk(dependency.ContractType, chosen, Array.Empty<string>(), dependencyPath + "/" + dependency.Slot, objects, request, issues, resources, checkFiles, depth + 1);
        }
        catch (Exception error) when (error is not OutOfMemoryException && error is not OperationCanceledException) { Error("ALG_INSPECTION_FAILED", "引擎配置检查失败：" + error.Message, error); }
        finally { objects.Remove(selection); }
    }
    /// <summary>在独立副本上执行引擎升级规则，任何失败均保留原选择。</summary>
    public VisionAlgorithmSelection Migrate(VisionAlgorithmSelection selection)
    {
        var clone = Clone(selection, new HashSet<VisionAlgorithmSelection>());
        void Upgrade(VisionAlgorithmSelection current)
        {
            var descriptor = _catalog.GetRequired(current.ImplementationId);
            var configuration = new VisionAlgorithmConfiguration(current.SettingsVersion, current.Settings);
            var upgraded = (descriptor.Factory as IVisionAlgorithmConfigurationMigrator)?.MigrateConfiguration(configuration);
            if (upgraded != null) { current.SettingsVersion = upgraded.SettingsVersion; current.Settings = upgraded.Settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal); }
            if (descriptor.Factory is IVisionAlgorithmConfigurationValidator validator)
            {
                var errors = validator.ValidateConfiguration(new VisionAlgorithmConfiguration(current.SettingsVersion, current.Settings));
                if (errors.Count > 0) throw new InvalidOperationException("配置无法升级：" + string.Join("；", errors));
            }
            // 只升级当前配置实际声明的依赖；停用分支保留原配置，不要求其插件已安装。
            foreach (var dependency in descriptor.Factory.GetDependencies(new VisionAlgorithmConfiguration(current.SettingsVersion, current.Settings)))
                if (current.Dependencies.TryGetValue(dependency.Slot, out var active)) Upgrade(active);
        }
        Upgrade(clone);
        var contract = _catalog.GetRequired(clone.ImplementationId).ContractType;
        var report = Analyze(new[] { new VisionAlgorithmRequest("migration", contract, clone) }, checkFiles: false);
        if (!report.Success) throw VisionAlgorithmExceptionDiagnostics.Attach(
            new InvalidOperationException("升级后的配置无效：" + string.Join("；", report.Issues.Select(i => i.Message))), report.Issues);
        return clone;
    }
    internal static VisionAlgorithmSelection Clone(VisionAlgorithmSelection selection, HashSet<VisionAlgorithmSelection> path)
    {
        if (selection == null || selection.Settings == null || selection.Dependencies == null) throw new InvalidOperationException("选择配置无效。");
        if (path.Count > 64) throw new InvalidOperationException("选择配置超过依赖深度预算。");
        if (!path.Add(selection)) throw new InvalidOperationException("选择配置形成引用循环。");
        try { return new VisionAlgorithmSelection { ImplementationId = selection.ImplementationId, SettingsVersion = selection.SettingsVersion,
            Settings = new Dictionary<string, string>(selection.Settings, StringComparer.Ordinal), Dependencies = selection.Dependencies.ToDictionary(p => p.Key, p => Clone(p.Value, path), StringComparer.Ordinal) }; }
        finally { path.Remove(selection); }
    }
}

/// <summary>保留旧异常类型的结构化诊断附件，调用方无需解析异常文本。</summary>
public static class VisionAlgorithmExceptionDiagnostics
{
    private const string Key = "DP.Vision.AlgorithmIssues";
    /// <summary>读取异常链上的算法问题。</summary>
    public static IReadOnlyList<VisionAlgorithmIssue> Read(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            if (current.Data[Key] is IReadOnlyList<VisionAlgorithmIssue> issues) return issues;
        return Array.Empty<VisionAlgorithmIssue>();
    }
    /// <summary>为宿主资源准备错误附加位置诊断，保留原异常类型。</summary>
    public static Exception Attach(Exception exception, IReadOnlyList<VisionAlgorithmIssue> issues)
    { exception.Data[Key] = Array.AsReadOnly(issues.ToArray()); return exception; }
}
