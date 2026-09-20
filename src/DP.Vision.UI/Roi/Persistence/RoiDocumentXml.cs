using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace DP.Vision.UI;

/// <summary>带版本号、与厂商无关的ROI配置XML；不接受运行时类型名、DTD或外部实体。</summary>
public static class RoiDocumentXml
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>序列化已提交配置，保留编辑约束、双精度原图坐标及Region游程。</summary>
    /// <param name = "document">待保存的不可变ROI文档，不包含未提交的手势预览。</param>
    /// <returns>明确标注image-edges坐标系和版本的XML文本。</returns>
    public static string Serialize(RoiDocument document)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var root = new XElement(
            "roi-document",
            new XAttribute("version", 1),
            new XAttribute("coordinates", "image-edges")
        );
        foreach (var roi in document.Rois)
        {
            root.Add(
                new XElement(
                    "roi",
                    new XAttribute("id", roi.Id),
                    new XAttribute("purpose", roi.Purpose),
                    new XAttribute("enabled", roi.Enabled),
                    new XAttribute("constraint", roi.Constraint),
                    WriteShape(roi.Shape)
                )
            );
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>读取受长度限制的XML文本；不支持的版本或几何直接拒绝，不近似替换。</summary>
    /// <param name = "xml">待解析文本，最大16Mi字符；禁止DTD和外部实体。</param>
    /// <returns>经过约束和几何预算校验的不可变ROI文档。</returns>
    public static RoiDocument Deserialize(string xml)
    {
        if (xml == null)
        {
            throw new ArgumentNullException(nameof(xml));
        }

        if (xml.Length > 16 * 1024 * 1024)
        {
            throw new ArgumentException("ROI XML exceeds size limit.");
        }

        using var reader = new StringReader(xml);
        return Deserialize(reader);
    }

    /// <summary>从文本读取器加载配置，使用XML字符预算限制内存，不关闭调用方读取器。</summary>
    /// <param name = "input">调用方拥有的读取器，只在调用期间借用。</param>
    /// <returns>经过完整校验的ROI文档；格式或预算不符时抛出异常。</returns>
    public static RoiDocument Deserialize(TextReader input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new FormatException("Missing ROI root.");
        Check(root, "roi-document", "version", "coordinates");
        if (Text(root, "version") != "1" || Text(root, "coordinates") != "image-edges")
        {
            throw new FormatException("Unsupported ROI schema or coordinates.");
        }

        var rois = root.Elements()
            .Select(element =>
            {
                Check(element, "roi", "id", "purpose", "enabled", "constraint");
                if (element.Elements().Count() != 1)
                {
                    throw new FormatException("Expected one geometry per ROI.");
                }

                if (!bool.TryParse(Text(element, "enabled"), out bool enabled))
                {
                    throw new FormatException("Invalid enabled flag.");
                }

                return new RoiDefinition(
                    Text(element, "id"),
                    ReadShape(element.Elements().Single()),
                    EnumValue<ERoiPurpose>(Text(element, "purpose")),
                    enabled,
                    EnumValue<ERoiConstraint>(Text(element, "constraint"))
                );
            })
            .ToArray();
        return new RoiDocument(rois);
    }

    private static XElement WriteShape(Geometry shape)
    {
        if (shape is RectangleGeometry rectangle)
        {
            return new XElement(
                "rectangle",
                A("cx", rectangle.Center.X),
                A("cy", rectangle.Center.Y),
                A("width", rectangle.Width),
                A("height", rectangle.Height),
                A("angle", rectangle.Angle)
            );
        }

        if (shape is EllipseGeometry ellipse)
        {
            return new XElement(
                "ellipse",
                A("cx", ellipse.Center.X),
                A("cy", ellipse.Center.Y),
                A("rx", ellipse.RadiusX),
                A("ry", ellipse.RadiusY),
                A("angle", ellipse.Angle)
            );
        }

        if (shape is ContourGeometry contour)
        {
            return new XElement(
                "contour",
                new XAttribute("closed", contour.Closed),
                new XAttribute("filled", contour.Filled),
                contour.Points.Select(p => new XElement("point", A("x", p.X), A("y", p.Y)))
            );
        }

        if (shape is RegionGeometry region)
        {
            return new XElement(
                "region",
                region.Runs.Select(r => new XElement(
                    "run",
                    new XAttribute("row", r.Row),
                    new XAttribute("start", r.Start),
                    new XAttribute("end-exclusive", r.EndExclusive)
                ))
            );
        }

        throw new NotSupportedException("Unsupported ROI geometry.");
    }

    private static Geometry ReadShape(XElement shape)
    {
        switch (shape.Name.ToString())
        {
            case "rectangle":
                Check(shape, "rectangle", "cx", "cy", "width", "height", "angle");
                Leaf(shape);
                return new RectangleGeometry(
                    new PointD(Number(shape, "cx"), Number(shape, "cy")),
                    Number(shape, "width"),
                    Number(shape, "height"),
                    Number(shape, "angle")
                );
            case "ellipse":
                Check(shape, "ellipse", "cx", "cy", "rx", "ry", "angle");
                Leaf(shape);
                return new EllipseGeometry(
                    new PointD(Number(shape, "cx"), Number(shape, "cy")),
                    Number(shape, "rx"),
                    Number(shape, "ry"),
                    Number(shape, "angle")
                );
            case "contour":
                Check(shape, "contour", "closed", "filled");
                if (
                    !bool.TryParse(Text(shape, "closed"), out bool closed)
                    || !bool.TryParse(Text(shape, "filled"), out bool filled)
                )
                {
                    throw new FormatException("Invalid contour flag.");
                }

                return new ContourGeometry(
                    shape
                        .Elements()
                        .Select(p =>
                        {
                            Check(p, "point", "x", "y");
                            Leaf(p);
                            return new PointD(Number(p, "x"), Number(p, "y"));
                        }),
                    closed,
                    filled
                );
            case "region":
                Check(shape, "region");
                return new RegionGeometry(
                    shape
                        .Elements()
                        .Select(r =>
                        {
                            Check(r, "run", "row", "start", "end-exclusive");
                            Leaf(r);
                            return new RegionRun(
                                Integer(r, "row"),
                                Integer(r, "start"),
                                Integer(r, "end-exclusive")
                            );
                        })
                );
            default:
                throw new FormatException("Unsupported ROI geometry: " + shape.Name);
        }
    }

    private static XAttribute A(string name, double value)
    {
        return new XAttribute(name, value.ToString("R", Culture));
    }

    private static string Text(XElement e, string name)
    {
        return e.Attribute(name)?.Value ?? throw new FormatException("Missing attribute: " + name);
    }

    private static double Number(XElement e, string name)
    {
        return double.Parse(Text(e, name), NumberStyles.Float, Culture);
    }

    private static int Integer(XElement e, string name)
    {
        return int.Parse(Text(e, name), NumberStyles.Integer, Culture);
    }

    private static T EnumValue<T>(string value)
        where T : struct
    {
        if (!Enum.TryParse(value, out T result) || !Enum.IsDefined(typeof(T), result))
        {
            throw new FormatException("Unsupported enum value.");
        }

        return result;
    }

    private static void Leaf(XElement element)
    {
        if (element.Elements().Any())
        {
            throw new FormatException("Unexpected geometry children.");
        }
    }

    private static void Check(XElement element, string name, params string[] attributes)
    {
        if (
            element.Name != name
            || element.Attributes().Any(a => !attributes.Contains(a.Name.ToString()))
            || element.Attributes().Count() != attributes.Length
            || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
        )
        {
            throw new FormatException("Unsupported ROI element structure: " + element.Name);
        }
    }
}
