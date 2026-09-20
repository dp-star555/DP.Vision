using System;
using System.Linq;
using DP.Vision.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>保证枚举采用E前缀，同时保留原成员名称、数值及整数底层类型。</summary>
[TestClass]
public sealed class EnumContractTests
{
    /// <summary>类型重命名不能改变像素布局、图层或ROI文档中的枚举值。</summary>
    /// <param name="type">需要检查的源码枚举类型。</param>
    /// <param name="expected">重构前记录的成员名称及数值，按数值顺序排列。</param>
    [TestMethod]
    [DataRow(typeof(ELayerKind), "Region=0,Xld=1,Roi=2,Annotation=3,Interaction=4")]
    [DataRow(typeof(EPixelLayout), "Gray8=0,Gray16=1,Bgr24=2,Rgb24=3,Bgra32=4,Rgba32=5")]
    [DataRow(typeof(ERoiPurpose), "Include=0,Exclude=1")]
    [DataRow(typeof(ERoiPointerAction), "Down=0,Move=1,Up=2")]
    [DataRow(
        typeof(ERoiTool),
        "Select=0,Rectangle=1,RotatedRectangle=2,Circle=3,Ellipse=4,Polygon=5,Polyline=6,Point=7,InsertVertex=8,DeleteVertex=9"
    )]
    [DataRow(typeof(ERoiConstraint), "None=0,AxisAligned=1,Circle=2")]
    [DataRow(typeof(ERoiHandleKind), "Size=0,Rotation=1,Vertex=2,Radius=3")]
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
