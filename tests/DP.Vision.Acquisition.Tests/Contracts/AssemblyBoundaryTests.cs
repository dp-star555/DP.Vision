using System;
using System.Linq;
using System.Reflection;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>公共采集契约的依赖边界回归：不得引用厂商SDK、Workflow或暴露裸指针。</summary>
[TestClass]
public sealed class AssemblyBoundaryTests
{
    private static readonly Assembly Contracts = typeof(VisionSourceReference).Assembly;

    /// <summary>公共采集程序集只引用中立图像库，不引用厂商SDK或Workflow。</summary>
    [TestMethod]
    public void AbstractionsAssembly_DoesNotReferenceVendorSdkOrWorkflow()
    {
        var referenced = Contracts.GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();
        var actual = string.Join(",", referenced);

        Assert.IsFalse(referenced.Any(name => name.StartsWith("HalconDotNet", StringComparison.OrdinalIgnoreCase)), actual);
        Assert.IsFalse(referenced.Any(name => name.StartsWith("DP.WorkFlow", StringComparison.Ordinal)), actual);
        Assert.IsFalse(referenced.Any(name => name.StartsWith("MvCamCtrl", StringComparison.OrdinalIgnoreCase)), actual);
        Assert.IsTrue(referenced.Contains("DP.Vision"), actual);
    }

    /// <summary>公共契约的每个公开成员都不得暴露厂商类型或裸指针。</summary>
    [TestMethod]
    public void PublicContractMembers_DoNotExposeVendorTypesOrPointers()
    {
        var inspected = 0;
        foreach (var type in Contracts.GetExportedTypes())
        {
            AssertVendorFree(type, type.FullName ?? type.Name);
            foreach (var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Type? memberType = member switch
                {
                    PropertyInfo property => property.PropertyType,
                    MethodInfo method => method.ReturnType,
                    FieldInfo field => field.FieldType,
                    _ => null
                };
                if (memberType is null)
                    continue;
                inspected++;
                AssertVendorFree(memberType, (type.FullName ?? type.Name) + "." + member.Name);
            }
        }

        // 防止"扫描到0个成员也算通过"的假绿。
        Assert.IsTrue(inspected > 40, $"仅检查到 {inspected} 个公开成员，边界扫描可能失效。");
    }

    private static void AssertVendorFree(Type type, string path)
    {
        var name = type.FullName ?? type.Name;
        Assert.IsFalse(name.StartsWith("HalconDotNet", StringComparison.Ordinal), path + " -> " + name);
        Assert.IsFalse(name.StartsWith("DP.WorkFlow", StringComparison.Ordinal), path + " -> " + name);
        Assert.IsFalse(name.StartsWith("MvCamCtrl", StringComparison.OrdinalIgnoreCase), path + " -> " + name);
        Assert.AreNotEqual(typeof(IntPtr), type, path + " 暴露了裸指针。");
        Assert.AreNotEqual(typeof(UIntPtr), type, path + " 暴露了裸指针。");

        foreach (var argument in type.GetGenericArguments())
            AssertVendorFree(argument, path);
    }
}
