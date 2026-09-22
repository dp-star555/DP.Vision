using System;
using System.Linq;
using System.Reflection;
using DP.Vision.Basler;
using DP.Vision.Halcon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Integration.Tests;

/// <summary>
/// deviceSettings 的**字段级**不变式：每个被解析器接受的字段，都必须在厂商私有绑定上有同名属性。
/// <para>
/// 为什么单独立一条：字段只进"配置摘要"（<c>CompositionId</c>）而没进私有绑定，是一种**静默失效**——
/// 改了配置 <c>CompositionId</c> 会变，看上去"生效了"，但设备从头到尾没被写过那个参数。
/// 现场表现就是"设置了但不生效"，属于最难查的一类。
/// </para>
/// <para>
/// 约定：解析器的字段常量（<c>private const string XxxKey</c>）必须对应绑定上同名的 PascalCase 属性
/// （<c>pixelFormat</c> → <c>PixelFormat</c>）。要新增 deviceSettings 字段，就得同时让绑定带上它；
/// 映射若确实需要改名，请连同本用例的约定一起显式改，不要让它悄悄退化成"只进摘要"。
/// </para>
/// </summary>
[TestClass]
public sealed class DeviceSettingsFieldsReachBindingTests
{
    /// <summary>两家厂商解析器接受的每个字段，都必须能在自己的私有绑定上读到。</summary>
    [TestMethod]
    public void EveryAcceptedField_ReachesTheVendorBinding()
    {
        AssertEveryFieldReachesBinding(typeof(BaslerDeviceSettingsParser), typeof(BaslerAcquisitionBinding));
        AssertEveryFieldReachesBinding(typeof(HalconDeviceSettingsParser), typeof(HalconAcquisitionBinding));
    }

    private static void AssertEveryFieldReachesBinding(Type parser, Type binding)
    {
        var keys = parser
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral
                            && field.FieldType == typeof(string)
                            && field.Name.EndsWith("Key", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        // 反例保护：确认真的扫到了字段清单，否则"零违规"可能只是什么都没找到。
        Assert.IsTrue(
            keys.Length >= 4,
            parser.Name + " 的字段常量只扫到 " + keys.Length + " 个，扫描方式可能已失效。");

        foreach (var key in keys)
        {
            var expected = char.ToUpperInvariant(key[0]) + key.Substring(1);
            Assert.IsNotNull(
                binding.GetProperty(expected),
                parser.Name + " 接受 deviceSettings 字段 \"" + key + "\"，但 " + binding.Name
                + " 上没有属性 " + expected + "；该字段只会进入配置摘要，永远到不了设备。");
        }
    }
}
