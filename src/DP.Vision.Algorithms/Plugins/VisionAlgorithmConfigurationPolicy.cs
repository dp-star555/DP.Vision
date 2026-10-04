using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>引擎提供的轻量配置规则；禁止创建实例、加载模型或打开设备。</summary>
public interface IVisionAlgorithmConfigurationValidator
{
    /// <summary>返回此配置的错误；只检查配置本身。</summary>
    IReadOnlyList<string> ValidateConfiguration(VisionAlgorithmConfiguration configuration);
}

/// <summary>引擎显式提供配置升级；失败不得修改原配方。</summary>
public interface IVisionAlgorithmConfigurationMigrator
{
    /// <summary>生成独立升级配置；不支持时返回空。</summary>
    VisionAlgorithmConfiguration? MigrateConfiguration(VisionAlgorithmConfiguration configuration);
}

/// <summary>已有版本 1 引擎的公共配置规则。</summary>
public static class VisionAlgorithmConfigurationRules
{
    /// <summary>检查版本、未知字段和必填字段，不访问文件或运行库。</summary>
    public static IReadOnlyList<string> ValidateVersionOne(VisionAlgorithmConfiguration configuration, IEnumerable<string> allowed, params string[] required)
    {
        var errors = new List<string>();
        if (configuration.SettingsVersion != 1) errors.Add("参数版本不支持；此实现需要版本 1。");
        var keys = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var key in configuration.Settings.Keys.Where(k => !keys.Contains(k))) errors.Add("未声明的初始化参数：" + key);
        foreach (var key in required)
            if (!configuration.Settings.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) errors.Add("缺少初始化参数：" + key);
        return errors;
    }
}
