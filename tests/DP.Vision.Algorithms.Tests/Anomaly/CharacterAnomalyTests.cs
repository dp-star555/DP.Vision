using System;
using System.Collections.Generic;
using System.Linq;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace DP.Vision.Algorithms.Tests;

/// <summary>逐字符异常检测：按行几何归一化、同键良品训练、缺墨检查、缺模型与归一化单元。</summary>
[TestClass]
public sealed class CharacterAnomalyTests
{
    private const string Text = "B1D3A2";

    /// <summary>合成一行文字（每字固定40像素格）；<paramref name = "faded"/>处的字符笔画变浅。</summary>
    private static IImageSource Line(int seed, int faded = -1)
    {
        using var image = new Mat(80, 280, MatType.CV_8UC1, Scalar.All(235));
        var random = new Random(seed);
        for (int i = 0; i < Text.Length; i++)
        {
            using var glyph = new Mat(80, 280, MatType.CV_8UC1, Scalar.All(0));
            Cv2.PutText(
                glyph,
                Text[i].ToString(),
                new Point(20 + i * 40 + random.Next(-1, 2), 55),
                HersheyFonts.HersheySimplex,
                1.2,
                Scalar.All(255),
                3,
                LineTypes.AntiAlias
            );
            image.SetTo(Scalar.All(i == faded ? 175 : 30), glyph);
        }

        using var noise = new Mat(80, 280, MatType.CV_8UC1);
        Cv2.Randu(noise, Scalar.All(0), Scalar.All(6));
        Cv2.Add(image, noise, image);
        var bytes = new byte[80 * 280];
        System.Runtime.InteropServices.Marshal.Copy(image.Data, bytes, 0, bytes.Length);
        return VisionImage.CopyFrom(new ImageInfo(280, 80, EPixelLayout.Gray8), bytes);
    }

    private static CharacterAnomalyCharacter[] Characters(bool keyed = true)
    {
        return Text.Select(
                (c, i) =>
                    new CharacterAnomalyCharacter(
                        c.ToString(),
                        i,
                        new PixelBounds(16 + i * 40, 14, 38, 52),
                        keyed ? c.ToString() : null
                    )
            )
            .ToArray();
    }

    private static Dictionary<string, CharacterAnomalyReference> Train(
        OpenCvCharacterAnomalyDetector detector,
        IReadOnlyList<IImageSource> good
    )
    {
        var patches = new OpenCvPatchAnomalyDetector();
        var trained = detector.Train(
            good.Select(g => new CharacterAnomalyLine(g, Characters())).ToArray(),
            new CharacterAnomalyOptions()
        );
        CollectionAssert.AreEqual(
            Text.Select(c => c.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            trained.Select(t => t.Key).ToArray()
        );
        Assert.IsTrue(
            trained.All(t =>
                t.InkThreshold != null && t.CellHeight == OpenCvCharacterAnomalyDetector.CellHeight
            )
        );
        return trained.ToDictionary(
            t => t.Key,
            t => new CharacterAnomalyReference(
                t.Model,
                patches,
                new PatchAnomalyOptions(
                    patchSize: t.Model.PatchSize,
                    stride: t.Options.Stride,
                    threshold: t.Model.Threshold,
                    minimumArea: t.Options.MinimumArea,
                    localRadius: t.Model.Radius
                ),
                t.CellWidth,
                t.CellHeight,
                t.InkThreshold
            )
        );
    }

    /// <summary>良品全部通过；变浅的字符报缺墨（原图坐标落在该字格内），其他字符不报；没有模型的字符标为缺模型。</summary>
    [TestMethod]
    public void FlagsFadedCharacterAndMissingModel()
    {
        var detector = new OpenCvCharacterAnomalyDetector();
        var good = Enumerable.Range(1, 5).Select(s => Line(s)).ToArray();
        try
        {
            var references = Train(detector, good);
            using (var clean = Line(11))
            using (
                var passed = detector.Inspect(
                    clean,
                    Characters(),
                    new PixelBounds(0, 0, 280, 80),
                    k => references[k]
                )
            )
            {
                Assert.IsTrue(
                    passed.Characters.All(o =>
                        o.Status == ECharacterAnomalyStatus.Compared
                        && o.Findings.All(f => f.Kind != EQualityFindingKind.Defect)
                    ),
                    string.Join(";", passed.Characters.SelectMany(o => o.Findings).Select(f => f.Message))
                );
                Assert.IsNotNull(passed.HeatMap);
                Assert.AreEqual(280, passed.HeatMap!.Info.Width);
            }

            using var faded = Line(12, faded: 3);
            using var result = detector.Inspect(
                faded,
                Characters(),
                new PixelBounds(0, 0, 280, 80),
                k => k == "A" ? null : references[k]
            );
            var three = result.Characters.Single(o => o.Character.Character == "3");
            var ink = three.Findings.First(f => f.Code == "ink_loss");
            Assert.IsTrue(
                ink.Bounds!.Value.X >= 16 + 3 * 40 && ink.Bounds.Value.X < 16 + 4 * 40,
                ink.Bounds.ToString()
            );
            Assert.IsTrue(three.InkLoss > three.InkThreshold);
            Assert.AreEqual(
                ECharacterAnomalyStatus.MissingModel,
                result.Characters.Single(o => o.Character.Character == "A").Status
            );
            Assert.IsTrue(
                result
                    .Characters.Where(o => o.Character.Character != "3" && o.Character.Character != "A")
                    .All(o => o.Findings.All(f => f.Kind != EQualityFindingKind.Defect))
            );
        }
        finally
        {
            foreach (var g in good)
            {
                g.Dispose();
            }
        }
    }

    /// <summary>归一化单元：同键同尺寸，与训练宽度一致；无键字符只参与行几何。</summary>
    [TestMethod]
    public void NormalizesCellsLikeTraining()
    {
        var detector = new OpenCvCharacterAnomalyDetector();
        using var a = Line(1);
        using var b = Line(2);
        using var test = Line(3);
        var training = new[]
        {
            new CharacterAnomalyLine(a, Characters()),
            new CharacterAnomalyLine(b, Characters()),
        };
        var keyedOnlyB = Characters(keyed: false)
            .Select((c, i) => i == 0 ? new CharacterAnomalyCharacter(c.Character, i, c.Bounds, "B") : c);
        var cells = detector.NormalizeCells(training, new[] { new CharacterAnomalyLine(test, keyedOnlyB) });
        try
        {
            Assert.AreEqual(12, cells.Count(c => c.Training));
            var tested = cells.Single(c => !c.Training);
            Assert.AreEqual(("B", 2, 0), (tested.Key, tested.Line, tested.Index));
            var trained = detector.Train(training, new CharacterAnomalyOptions());
            foreach (var group in cells.GroupBy(c => c.Key))
            {
                Assert.AreEqual(
                    1,
                    group.Select(c => (c.Image.Info.Width, c.Image.Info.Height)).Distinct().Count()
                );
                Assert.AreEqual(
                    trained.Single(t => t.Key == group.Key).CellWidth,
                    group.First().Image.Info.Width
                );
            }
        }
        finally
        {
            foreach (var c in cells)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>
    /// 同一原图中重复出现的字符（孪生样本）：按样本留一时被孪生样本解释、阈值偏紧；按来源留一时阈值不低于按样本留一；
    /// 不启用时与普通训练逐字节相同。
    /// </summary>
    [TestMethod]
    public void SourceLeaveOneOutIgnoresTwins()
    {
        var detector = new OpenCvCharacterAnomalyDetector();
        using var a = Line(1);
        using var b = Line(2);
        // 每张图两行完全相同的字符：同一来源的孪生样本。
        var lines = new[] { a, a, b, b }.Select(g => new CharacterAnomalyLine(g, Characters())).ToArray();
        var sample = detector.Train(lines, new CharacterAnomalyOptions());
        var source = detector.Train(lines, new CharacterAnomalyOptions(sourceLeaveOneOut: true));
        var plain = detector.Train(lines, new CharacterAnomalyOptions(sourceLeaveOneOut: false));
        for (int i = 0; i < sample.Count; i++)
        {
            CollectionAssert.AreEqual(sample[i].Model.ToBytes(), plain[i].Model.ToBytes());
            Assert.IsTrue(source[i].Model.Threshold > sample[i].Model.Threshold, source[i].Key);
            StringAssert.Contains(source[i].Model.Calibration, "按来源留一，2个来源");
        }
    }
}
