using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

/// <summary>模板匹配结果只输出位姿测量值：中心、角度（度）、缩放、参考点和参考方向。</summary>
[TestClass]
public sealed class TemplatePoseResultTests
{
    private static readonly TemplateReference Center = new TemplateReference(10, 8, 0, "center");

    /// <summary>参考原点偏离模板中心且带方向时，参考点和方向随位姿旋转缩放。</summary>
    [TestMethod]
    public void ReferencePointAndDirectionFollowPose()
    {
        var reference = new TemplateReference(20, 8, Math.PI / 2, "offset");
        var pose = new TemplatePoseTransform(20, 16, new PointD(100, 50), Math.PI / 2, 2);
        var result = new TemplatePoseResult("frame", "template", .95, pose, reference);
        Assert.IsTrue(result.Found);
        Assert.AreEqual(100, result.CenterX); Assert.AreEqual(50, result.CenterY);
        Assert.AreEqual(90, result.AngleDegrees, 1e-9); Assert.AreEqual(2, result.Scale);
        // 参考原点在模板中心右侧10像素；旋转90°、放大2倍后位于中心下方20像素。
        Assert.AreEqual(100, result.ReferenceX, 1e-9); Assert.AreEqual(70, result.ReferenceY, 1e-9);
        Assert.AreEqual(180, result.ReferenceAngleDegrees, 1e-9);
        var axis = result.ReferenceToImage!.Map(new Coordinate2D(1, 0));
        Assert.AreEqual(98, axis.X, 1e-9); Assert.AreEqual(70, axis.Y, 1e-9);
        Assert.AreEqual(result.ReferenceX, result.ReferencePoint!.ImagePosition.X, 1e-12);
        Assert.AreEqual(result.CenterX, result.CenterPoint!.ImagePosition.X);
        Assert.AreEqual(4, result.DisplayGeometry.Count);
        StringAssert.Contains(result.Summary, "角度 90.00°");
    }

    /// <summary>角度以度输出，顺时针为正，归一到(-180,180]。</summary>
    [TestMethod]
    [DataRow(0d, 0d)]
    [DataRow(-Math.PI / 6, -30d)]
    [DataRow(Math.PI, 180d)]
    [DataRow(-Math.PI, 180d)]
    [DataRow(3 * Math.PI / 2, -90d)]
    public void AngleIsNormalizedDegrees(double radians, double degrees)
    {
        var result = new TemplatePoseResult("frame", "template", 1, new TemplatePoseTransform(20, 16, new PointD(50, 50), radians, 1), Center);
        Assert.AreEqual(degrees, result.AngleDegrees, 1e-9);
        Assert.AreEqual(degrees, result.ReferenceAngleDegrees, 1e-9);
    }

    /// <summary>未找到时不伪装零位姿：数值为NaN，点和映射为空。</summary>
    [TestMethod]
    public void NotFoundHasNoPose()
    {
        var result = new TemplatePoseResult("frame", "template", .4, null, Center);
        Assert.IsFalse(result.Found);
        foreach (var value in new[] { result.CenterX, result.CenterY, result.AngleDegrees, result.Scale, result.ReferenceX, result.ReferenceY, result.ReferenceAngleDegrees })
            Assert.IsTrue(double.IsNaN(value));
        Assert.IsNull(result.CenterPoint); Assert.IsNull(result.ReferencePoint); Assert.IsNull(result.ReferenceToImage); Assert.IsNull(result.MatchGeometry);
        Assert.AreEqual(0, result.DisplayGeometry.Count);
        StringAssert.StartsWith(result.Summary, "未找到");
        Assert.ThrowsExactly<ArgumentNullException>(() => new TemplatePoseResult("frame", "template", 1, null, null!));
    }

    /// <summary>图像模板的参考取矩形中心；签名只随矩形内像素变化。</summary>
    [TestMethod]
    public void ImageReferenceUsesBoundsCenterAndPixelSignature()
    {
        var pixels = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        using var a = VisionImage.CopyFrom(new ImageInfo(8, 6, EPixelLayout.Gray8), pixels);
        var changedInside = (byte[])pixels.Clone(); changedInside[2 * 8 + 3] ^= 1;
        var changedOutside = (byte[])pixels.Clone(); changedOutside[0] ^= 1;
        using var inside = VisionImage.CopyFrom(new ImageInfo(8, 6, EPixelLayout.Gray8), changedInside);
        using var outside = VisionImage.CopyFrom(new ImageInfo(8, 6, EPixelLayout.Gray8), changedOutside);
        var bounds = new PixelBounds(2, 1, 4, 3);
        var reference = TemplateReference.FromImage(a, bounds);
        Assert.AreEqual(2, reference.OriginX); Assert.AreEqual(1.5, reference.OriginY); Assert.AreEqual(0, reference.AxisAngleRadians);
        Assert.AreEqual(reference.Signature, TemplateReference.FromImage(outside, bounds).Signature);
        Assert.AreNotEqual(reference.Signature, TemplateReference.FromImage(inside, bounds).Signature);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TemplateReference.FromImage(a, new PixelBounds(6, 0, 4, 3)));
        Assert.ThrowsExactly<OperationCanceledException>(() => TemplateReference.FromImage(a, bounds, new System.Threading.CancellationToken(true)));
    }

    /// <summary>资源模板的参考换算到裁剪坐标；原点、方向、身份或版本变化都改变签名，绑定后的坐标定义随之变化。</summary>
    [TestMethod]
    public void DefinitionReferenceTracksGeometryAndIdentity()
    {
        VisionTemplateDefinition Definition() => new VisionTemplateDefinition
        {
            SourceWidth = 100, SourceHeight = 80, X = 10, Y = 20, Width = 30, Height = 20,
            OriginX = 25, OriginY = 30, AxisAngleRadians = .5, ReferenceIdentity = "part"
        };
        var reference = Definition().Reference();
        Assert.AreEqual(15, reference.OriginX); Assert.AreEqual(10, reference.OriginY); Assert.AreEqual(.5, reference.AxisAngleRadians);
        Assert.AreEqual(reference.Signature, Definition().Reference().Signature);
        var moved = Definition(); moved.OriginX = 26;
        var turned = Definition(); turned.AxisAngleRadians = .6;
        var renamed = Definition(); renamed.ReferenceIdentity = "other";
        var versioned = Definition(); versioned.ReferenceVersion = 2;
        foreach (var changed in new[] { moved, turned, renamed, versioned })
            Assert.AreNotEqual(reference.Signature, changed.Reference().Signature);

        var business = new VisionCoordinateDefinition("part", "零件", 3);
        var bound = reference.Bind(business);
        Assert.AreEqual("part", bound.Id); Assert.AreEqual(3, bound.Version); Assert.AreEqual(business.Unit, bound.Unit);
        Assert.AreNotEqual(business.Signature, bound.Signature);
        Assert.AreEqual(bound.Signature, Definition().Reference().Bind(business).Signature);
        Assert.AreNotEqual(bound.Signature, moved.Reference().Bind(business).Signature);
    }
}
