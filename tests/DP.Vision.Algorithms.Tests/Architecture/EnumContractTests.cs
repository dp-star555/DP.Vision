using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>算法枚举重命名的数值契约回归。</summary>
[TestClass]
public sealed class EnumContractTests
{
    /// <summary>保留完成状态、证据角色和二值化模式的既有含义。</summary>
    /// <param name="type">要检查的中立算法枚举。</param>
    /// <param name="expected">重构前的成员名称及整数值。</param>
    [TestMethod]
    [DataRow(
        typeof(EAlgorithmStatus),
        "Completed=0,UnsupportedInput=1,InsufficientEvidence=2,NotRequested=3"
    )]
    [DataRow(typeof(EQualityFindingKind), "Information=0,Blocker=1,Defect=2")]
    [DataRow(typeof(EGlyphBinarization), "Otsu=0,Fixed=1,Midpoint=2")]
    public void NamesAndValuesRemainExplicit(Type type, string expected)
    {
        Assert.IsTrue(type.IsEnum && type.Name.StartsWith("E", StringComparison.Ordinal));
        Assert.AreEqual(typeof(int), Enum.GetUnderlyingType(type));
        string actual = string.Join(
            ",",
            Enum.GetNames(type).Select(name => name + "=" + Convert.ToInt32(Enum.Parse(type, name)))
        );
        Assert.AreEqual(expected, actual);
    }
}
