using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>验证命名颜色的ARGB顺序及默认显示兼容性，不将颜色当作判定状态。</summary>
[TestClass]
public sealed class VisionColorsTests
{
    /// <summary>命名颜色保留标准ARGB数值及历史显示配色。</summary>
    /// <param name="name">颜色常量名称。</param>
    /// <param name="expected">预期打包值。</param>
    [TestMethod]
    [DataRow("Transparent", 0x00000000u)]
    [DataRow("Red", 0xFFFF0000u)]
    [DataRow("Lime", 0xFF00FF00u)]
    [DataRow("Blue", 0xFF0000FFu)]
    [DataRow("Rose", 0xFFFF3388u)]
    [DataRow("Gray", 0xFF808080u)]
    [DataRow("RoyalBlue", 0xFF4169E1u)]
    [DataRow("ForestGreen", 0xFF228B22u)]
    [DataRow("Crimson", 0xFFDC143Cu)]
    [DataRow("DarkOrange", 0xFFFF8C00u)]
    public void NamedColorsPreserveArgb(string name, uint expected)
    {
        var field = typeof(VisionColors).GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(field);
        Assert.AreEqual(expected, (uint)field.GetRawConstantValue()!);
    }

    /// <summary>默认颜色不变，客户仍可提供半透明自定义ARGB。</summary>
    [TestMethod]
    public void VisualSupportsDefaultNamedAndCustomColors()
    {
        var shape = new RectangleGeometry(new PointD(10, 10), 2, 2);
        Assert.AreEqual(0xFFFF3388u, new Visual("default", shape).Argb);
        Assert.AreEqual(0xFFFF0000u, new Visual("named", shape, VisionColors.Red).Argb);
        Assert.AreEqual(0x80123456u, new Visual("custom", shape, 0x80123456).Argb);
    }
}
