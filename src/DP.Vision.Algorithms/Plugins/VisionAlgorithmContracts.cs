using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Algorithms;

/// <summary>接口的稳定能力身份与逻辑分类，独立于引擎和 DLL。</summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class VisionCapabilityAttribute : Attribute
{
    /// <summary>声明能力。</summary>
    public VisionCapabilityAttribute(string id, string category, string displayName)
    { Id = id; Category = category; DisplayName = displayName; }
    /// <summary>持久化的稳定身份。</summary>
    public string Id { get; }
    /// <summary>逻辑分类。</summary>
    public string Category { get; }
    /// <summary>显示名称。</summary>
    public string DisplayName { get; }
}

/// <summary>引擎入口，只贡献描述与工厂，不在登记阶段申请算法资源。</summary>
public interface IVisionAlgorithmModule
{
    /// <summary>模块稳定身份。</summary>
    string ExtensionId { get; }
    /// <summary>向本模块私有候选登记器贡献实现。</summary>
    void Register(IVisionAlgorithmRegistration registrations);
}

/// <summary>候选算法登记器，模块贡献成功后才合并。</summary>
public interface IVisionAlgorithmRegistration
{
    /// <summary>登记一个实现及其工厂。</summary>
    void Add(VisionAlgorithmDescriptor descriptor);
}

/// <summary>算法资源的实例共享及调用策略。</summary>
public enum EVisionAlgorithmSharing
{
    /// <summary>每个绑定独立创建。</summary>
    Exclusive,
    /// <summary>可以共享，所有使用同一资源的调用串行。</summary>
    SharedSerial,
    /// <summary>可以共享并并发调用。</summary>
    SharedConcurrent
}

/// <summary>节点保存的实现选择；准备过程深复制，运行期间不读取可变配置。</summary>
public sealed class VisionAlgorithmSelection
{
    /// <summary>明确选择的实现；为空时仅可使用宿主明确提供的默认值。</summary>
    public string ImplementationId { get; set; } = string.Empty;
    /// <summary>实现参数结构版本。</summary>
    public int SettingsVersion { get; set; } = 1;
    /// <summary>实现专有初始化参数；数值用 invariant 格式持久化。</summary>
    public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>按工厂声明的槽位明确选择依赖。</summary>
    public Dictionary<string, VisionAlgorithmSelection> Dependencies { get; set; } = new Dictionary<string, VisionAlgorithmSelection>(StringComparer.Ordinal);
}

/// <summary>工厂看到的不可变初始化配置；标准调用参数不属于此配置。</summary>
public sealed class VisionAlgorithmConfiguration
{
    /// <summary>复制初始化配置。</summary>
    public VisionAlgorithmConfiguration(int version, IReadOnlyDictionary<string, string> settings)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        SettingsVersion = version;
        Settings = new ReadOnlyDictionary<string, string>(settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
    /// <summary>结构版本。</summary>
    public int SettingsVersion { get; }
    /// <summary>不可变初始化设置。</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }
}

/// <summary>工厂声明的算法依赖槽位。</summary>
public sealed class VisionAlgorithmDependency
{
    /// <summary>声明依赖接口。</summary>
    public VisionAlgorithmDependency(string slot, Type contractType)
    { Slot = slot; ContractType = contractType; }
    /// <summary>依赖槽位。</summary>
    public string Slot { get; }
    /// <summary>所需能力接口。</summary>
    public Type ContractType { get; }
}

/// <summary>实现特有初始化参数的基础编辑描述。</summary>
public sealed class VisionAlgorithmParameter
{
    /// <summary>创建参数描述。</summary>
    public VisionAlgorithmParameter(string id, string displayName, Type valueType, string? defaultValue = null, string? description = null,
        double? minimum = null, double? maximum = null, bool isFilePath = false)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("参数身份和名称不能为空。");
        if (minimum > maximum || (minimum.HasValue && (double.IsNaN(minimum.Value) || double.IsInfinity(minimum.Value)))
            || (maximum.HasValue && (double.IsNaN(maximum.Value) || double.IsInfinity(maximum.Value)))) throw new ArgumentException("参数范围无效。");
        Id = id; DisplayName = displayName; ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        DefaultValue = defaultValue; Description = description; Minimum = minimum; Maximum = maximum; IsFilePath = isFilePath;
    }
    /// <summary>持久字段身份。</summary>
    public string Id { get; }
    /// <summary>显示名称。</summary>
    public string DisplayName { get; }
    /// <summary>值类型，首版支持基本类型。</summary>
    public Type ValueType { get; }
    /// <summary>invariant 格式默认值。</summary>
    public string? DefaultValue { get; }
    /// <summary>用途、取值和资源角色说明。</summary>
    public string? Description { get; }
    /// <summary>可选数值下限。</summary>
    public double? Minimum { get; }
    /// <summary>可选数值上限。</summary>
    public double? Maximum { get; }
    /// <summary>文件资源路径，供编辑器选择文件；存在性由工厂最终验证。</summary>
    public bool IsFilePath { get; }
    /// <summary>数值按弧度存储；编辑器以度显示和提交，数值范围及默认值仍为弧度。</summary>
    public bool DisplayRadiansAsDegrees { get; set; }
}

/// <summary>算法工厂，明确解释设置、依赖和资源身份。</summary>
public interface IVisionAlgorithmFactory
{
    /// <summary>此工厂提供的类型化算法接口。</summary>
    Type ContractType { get; }
    /// <summary>在创建前声明依赖，用于绑定和循环检查。</summary>
    IReadOnlyList<VisionAlgorithmDependency> GetDependencies(VisionAlgorithmConfiguration configuration);
    /// <summary>校验配置并捕获资源快照；返回延迟创建描述。</summary>
    Task<VisionAlgorithmActivation> PrepareAsync(VisionAlgorithmConfiguration configuration,
        IReadOnlyDictionary<string, object> dependencies, CancellationToken cancellationToken);
}

/// <summary>一份已经校验、捕获输入快照的延迟创建描述。</summary>
public sealed class VisionAlgorithmActivation
{
    /// <summary>声明资源身份与创建方式。</summary>
    public VisionAlgorithmActivation(string resourceIdentity, EVisionAlgorithmSharing sharing,
        Func<CancellationToken, Task<VisionAlgorithmResource>> create)
    {
        if (string.IsNullOrWhiteSpace(resourceIdentity)) throw new ArgumentException("资源身份不能为空。", nameof(resourceIdentity));
        if (!Enum.IsDefined(typeof(EVisionAlgorithmSharing), sharing)) throw new ArgumentOutOfRangeException(nameof(sharing));
        ResourceIdentity = resourceIdentity; Sharing = sharing; CreateAsync = create ?? throw new ArgumentNullException(nameof(create));
    }
    /// <summary>工厂规范化的初始化/资源身份，不包含每次调用参数。</summary>
    public string ResourceIdentity { get; }
    /// <summary>共享策略。</summary>
    public EVisionAlgorithmSharing Sharing { get; }
    /// <summary>捕获快照的创建函数，失败时工厂清理半成品。</summary>
    public Func<CancellationToken, Task<VisionAlgorithmResource>> CreateAsync { get; }
}

/// <summary>运行时拥有的算法对象及其销毁行为。</summary>
public sealed class VisionAlgorithmResource : IDisposable
{
    private Action? _release;
    /// <summary>包装类型化算法对象；未提供销毁函数时使用 IDisposable。</summary>
    public VisionAlgorithmResource(object instance, Action? release = null)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _release = release ?? (() => (instance as IDisposable)?.Dispose());
    }
    /// <summary>算法实例，仅供工厂依赖装配及运行时调用。</summary>
    public object Instance { get; }
    /// <summary>幂等归还资源。</summary>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>引擎提供的不可变实现描述。</summary>
public sealed class VisionAlgorithmDescriptor
{
    /// <summary>创建实现描述。</summary>
    public VisionAlgorithmDescriptor(string implementationId, string engine, string version, IVisionAlgorithmFactory factory,
        IEnumerable<string>? features = null, IEnumerable<VisionAlgorithmParameter>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(implementationId) || string.IsNullOrWhiteSpace(engine) || string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("实现身份、引擎与版本不能为空。");
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ContractType = factory.ContractType;
        var capability = (VisionCapabilityAttribute?)Attribute.GetCustomAttribute(ContractType, typeof(VisionCapabilityAttribute));
        if (!ContractType.IsInterface || capability == null || string.IsNullOrWhiteSpace(capability.Id))
            throw new ArgumentException("算法工厂必须提供带能力元数据的接口。", nameof(factory));
        ImplementationId = implementationId; Engine = engine; Version = version; CapabilityId = capability.Id;
        Category = capability.Category; DisplayName = capability.DisplayName;
        Features = Array.AsReadOnly((features ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray());
        Parameters = Array.AsReadOnly((parameters ?? Array.Empty<VisionAlgorithmParameter>()).ToArray());
        if (Parameters.GroupBy(parameter => parameter.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("实现重复声明初始化参数。");
    }
    /// <summary>实现身份。</summary>
    public string ImplementationId { get; }
    /// <summary>引擎名称。</summary>
    public string Engine { get; }
    /// <summary>实现发布版本。</summary>
    public string Version { get; }
    /// <summary>能力身份。</summary>
    public string CapabilityId { get; }
    /// <summary>能力接口。</summary>
    public Type ContractType { get; }
    /// <summary>逻辑分类。</summary>
    public string Category { get; }
    /// <summary>能力名称。</summary>
    public string DisplayName { get; }
    /// <summary>附加特征。</summary>
    public IReadOnlyList<string> Features { get; }
    /// <summary>特有初始化参数。</summary>
    public IReadOnlyList<VisionAlgorithmParameter> Parameters { get; }
    /// <summary>创建工厂。</summary>
    public IVisionAlgorithmFactory Factory { get; }
}

/// <summary>简单类型化工厂；复杂引擎可自行实现工厂接口。</summary>
public sealed class VisionAlgorithmFactory<T> : IVisionAlgorithmFactory, IVisionAlgorithmConfigurationValidator, IVisionAlgorithmConfigurationMigrator where T : class
{
    private readonly Func<VisionAlgorithmConfiguration, IReadOnlyDictionary<string, object>, CancellationToken, Task<VisionAlgorithmActivation>> _prepare;
    private readonly Func<VisionAlgorithmConfiguration, IReadOnlyList<VisionAlgorithmDependency>> _dependencies;
    private Func<VisionAlgorithmConfiguration, IReadOnlyList<string>>? _validate;
    private Func<VisionAlgorithmConfiguration, VisionAlgorithmConfiguration?>? _migrate;
    /// <summary>以显式委托提供校验与创建。</summary>
    public VisionAlgorithmFactory(Func<VisionAlgorithmConfiguration, IReadOnlyDictionary<string, object>, CancellationToken, Task<VisionAlgorithmActivation>> prepare,
        Func<VisionAlgorithmConfiguration, IReadOnlyList<VisionAlgorithmDependency>>? dependencies = null)
    { _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare)); _dependencies = dependencies ?? (_ => Array.Empty<VisionAlgorithmDependency>()); }
    /// <inheritdoc/>
    public Type ContractType => typeof(T);
    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmDependency> GetDependencies(VisionAlgorithmConfiguration configuration) => _dependencies(configuration);
    /// <inheritdoc/>
    public Task<VisionAlgorithmActivation> PrepareAsync(VisionAlgorithmConfiguration configuration, IReadOnlyDictionary<string, object> dependencies, CancellationToken cancellationToken)
        => _prepare(configuration, dependencies, cancellationToken);
    /// <summary>生成具有配置校验和可选升级规则的新工厂，不修改原工厂。</summary>
    public VisionAlgorithmFactory<T> WithConfigurationPolicy(Func<VisionAlgorithmConfiguration, IReadOnlyList<string>> validate,
        Func<VisionAlgorithmConfiguration, VisionAlgorithmConfiguration?>? migrate = null) => new VisionAlgorithmFactory<T>(_prepare, _dependencies)
        { _validate = validate ?? throw new ArgumentNullException(nameof(validate)), _migrate = migrate };
    /// <inheritdoc/>
    public IReadOnlyList<string> ValidateConfiguration(VisionAlgorithmConfiguration configuration) => _validate?.Invoke(configuration) ?? Array.Empty<string>();
    /// <inheritdoc/>
    public VisionAlgorithmConfiguration? MigrateConfiguration(VisionAlgorithmConfiguration configuration) => _migrate?.Invoke(configuration);
    /// <summary>创建没有专有设置或资源依赖的并发安全算法工厂。</summary>
    public static VisionAlgorithmFactory<T> Stateless(Func<T> create, Action? validateEnvironment = null) => new VisionAlgorithmFactory<T>((configuration, dependencies, token) =>
    {
        token.ThrowIfCancellationRequested();
        if (configuration.SettingsVersion != 1 || configuration.Settings.Count != 0) throw new ArgumentException("此实现只支持版本1的空初始化配置。");
        validateEnvironment?.Invoke();
        return Task.FromResult(new VisionAlgorithmActivation("stateless:v1", EVisionAlgorithmSharing.SharedConcurrent,
            cancellation => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(new VisionAlgorithmResource(create())); }));
    }).WithConfigurationPolicy(configuration => VisionAlgorithmConfigurationRules.ValidateVersionOne(configuration, Array.Empty<string>()));
}
