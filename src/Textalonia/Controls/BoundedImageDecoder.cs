using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Textalonia.Controls;

/// <summary>
/// Decodes bounded raster images and a static SVG subset without opening files or resolving
/// external references. SVG supports groups, paths and basic geometric shapes, solid paints,
/// opacity, and affine transforms. Unsupported SVG content fails explicitly. EMF/WMF must be
/// retained as originals with a separately supplied preview or converted by the host.
/// The caller owns the returned bitmap. An Avalonia rendering backend must be initialized.
/// </summary>
public static class BoundedImageDecoder
{
    public static Bitmap Decode(ReadOnlyMemory<byte> data, string? mediaType = null, InlineImageOptions? options = null)
    {
        options ??= new();
        options.Validate();
        if (data.Length == 0 || data.Length > options.MaximumEncodedBytes)
            throw new InvalidDataException("The encoded image is empty or exceeds its byte limit.");
        if (IsSvg(data.Span, mediaType)) return SvgImage.Decode(data, options);
        var (width, height) = ImageDimensions.Read(data.Span);
        CheckDimensions(width, height, options);
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        var bitmap = new Bitmap(stream);
        try
        {
            CheckDimensions(bitmap.PixelSize.Width, bitmap.PixelSize.Height, options);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static void CheckDimensions(int width, int height, InlineImageOptions options)
    {
        if (width < 1 || height < 1 || width > options.MaximumDimension || height > options.MaximumDimension ||
            (long)width * height > options.MaximumDecodedPixels)
            throw new InvalidDataException("The decoded image exceeds its dimension or pixel limit.");
    }

    private static bool IsSvg(ReadOnlySpan<byte> data, string? mediaType)
    {
        if (string.Equals(mediaType?.Split(';')[0].Trim(), "image/svg+xml", StringComparison.OrdinalIgnoreCase)) return true;
        if (data.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) data = data[3..];
        foreach (var value in data)
        {
            if (value is 9 or 10 or 13 or 32) continue;
            return value == '<';
        }
        return false;
    }

    private static class SvgImage
    {
        private const string Namespace = "http://www.w3.org/2000/svg";
        private const double CoordinateLimit = 1_000_000;
        private static readonly Regex NumberPattern = new(@"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant);
        private static readonly Regex TransformPattern = new(@"([A-Za-z]+)\s*\(([^()]*)\)", RegexOptions.CultureInvariant);
        private sealed record Paint(string Fill = "black", string Stroke = "none", double StrokeWidth = 1,
            double FillOpacity = 1, double StrokeOpacity = 1, FillRule FillRule = FillRule.NonZero);

        internal static Bitmap Decode(ReadOnlyMemory<byte> data, InlineImageOptions options)
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = options.MaximumEncodedBytes, IgnoreComments = true,
            });
            // Check depth before building an XML tree so deeply nested input is rejected before
            // traversal or a drawing backend can consume it.
            var elements = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.ProcessingInstruction)
                    throw new InvalidDataException("SVG processing instructions are unsupported.");
                if (reader.NodeType == XmlNodeType.Element && (++elements > options.MaximumVectorElements || reader.Depth >= options.MaximumVectorDepth))
                    throw new InvalidDataException("SVG exceeds its element or nesting limit.");
            }
            stream.Position = 0;
            using var treeReader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = options.MaximumEncodedBytes, IgnoreComments = true,
            });
            var root = XDocument.Load(treeReader).Root ?? throw new InvalidDataException("SVG has no root element.");
            if (root.Name.LocalName != "svg" || (root.Name.NamespaceName != Namespace && root.Name.NamespaceName != ""))
                throw new InvalidDataException("An SVG root element is required.");
            var pathCharacters = 0;
            Validate(root, root, options, ref pathCharacters);
            if (root.Attribute("viewBox") is { Value.Length: > 1024 })
                throw new InvalidDataException("SVG viewBox exceeds its coordinate-list limit.");
            var viewBox = root.Attribute("viewBox") is { } box ? Numbers(box.Value) : null;
            if (viewBox is not null && (viewBox.Length != 4 || viewBox[2] <= 0 || viewBox[3] <= 0))
                throw new InvalidDataException("SVG viewBox requires positive width and height.");
            var width = Length(root, "width", viewBox?[2] ?? 300);
            var height = Length(root, "height", viewBox?[3] ?? 150);
            if (width <= 0 || height <= 0 || width > options.MaximumDimension || height > options.MaximumDimension)
                throw new InvalidDataException("SVG dimensions exceed their limit.");
            var pixelWidth = (int)Math.Ceiling(width);
            var pixelHeight = (int)Math.Ceiling(height);
            CheckDimensions(pixelWidth, pixelHeight, options);
            var transform = Matrix.Identity;
            if (viewBox is not null)
            {
                var sx = width / viewBox[2];
                var sy = height / viewBox[3];
                if ((string?)root.Attribute("preserveAspectRatio") != "none") sx = sy = Math.Min(sx, sy);
                transform = Matrix.CreateTranslation(-viewBox[0], -viewBox[1]) * Matrix.CreateScale(sx, sy) *
                    Matrix.CreateTranslation((width - viewBox[2] * sx) / 2, (height - viewBox[3] * sy) / 2);
            }
            var bitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
            try
            {
                using var context = bitmap.CreateDrawingContext();
                using var clip = context.PushClip(new Rect(0, 0, width, height));
                using var scale = context.PushTransform(transform);
                CheckTransform(transform);
                Draw(context, root, new Paint(), transform);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private static void Validate(XElement element, XElement root, InlineImageOptions options, ref int pathCharacters)
        {
            var name = element.Name.LocalName;
            if (element.Name.Namespace != root.Name.Namespace || (name == "svg" && element != root))
                throw new InvalidDataException("Nested SVG documents and foreign namespaces are unsupported.");
            var shapeAttributes = name switch
            {
                "svg" => " width height viewBox preserveAspectRatio version ",
                "g" => " ", "rect" => " x y width height rx ry ",
                "circle" => " cx cy r ", "ellipse" => " cx cy rx ry ",
                "line" => " x1 y1 x2 y2 ", "path" => " d ",
                "polygon" or "polyline" => " points ", "title" or "desc" => " ",
                _ => throw new InvalidDataException($"Unsupported SVG element '{name}'."),
            };
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                var key = attribute.Name.LocalName;
                if (attribute.Name.NamespaceName != "" ||
                    !(" id fill fill-rule stroke stroke-width fill-opacity stroke-opacity opacity transform ".Contains($" {key} ", StringComparison.Ordinal) ||
                    shapeAttributes.Contains($" {key} ", StringComparison.Ordinal)))
                    throw new InvalidDataException($"Unsupported SVG attribute '{key}'.");
                if (key is "d" or "points")
                {
                    pathCharacters = checked(pathCharacters + attribute.Value.Length);
                    if (pathCharacters > options.MaximumVectorPathCharacters)
                        throw new InvalidDataException("SVG path data exceeds its complexity limit.");
                    if (key == "d")
                    {
                        // Validate magnitudes before passing path data to the native geometry
                        // implementation; the aggregate character limit bounds path allocation.
                        var remainder = NumberPattern.Replace(attribute.Value, match => { Number(match.Value); return ""; });
                        if (remainder.Any(c => !char.IsWhiteSpace(c) && c != ',' && !"MmLlHhVvCcSsQqTtAaZz".Contains(c)))
                            throw new InvalidDataException("Malformed SVG path data.");
                    }
                }
            }
            if (element.Attribute("preserveAspectRatio") is { } ratio && ratio.Value is not ("none" or "xMidYMid" or "xMidYMid meet"))
                throw new InvalidDataException("Unsupported SVG aspect ratio mode.");
            if (name is not ("svg" or "g" or "title" or "desc") && element.HasElements)
                throw new InvalidDataException("SVG shape children are unsupported.");
            if (name is "title" or "desc" && element.HasElements)
                throw new InvalidDataException("SVG metadata must contain text only.");
            if (name is not ("title" or "desc") && element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                throw new InvalidDataException("SVG text rendering is unsupported.");
            foreach (var child in element.Elements()) Validate(child, root, options, ref pathCharacters);
        }

        private static void Draw(DrawingContext context, XElement element, Paint inherited, Matrix inheritedTransform)
        {
            if (element.Name.LocalName is "title" or "desc") return;
            var paint = new Paint((string?)element.Attribute("fill") ?? inherited.Fill,
                (string?)element.Attribute("stroke") ?? inherited.Stroke, Length(element, "stroke-width", inherited.StrokeWidth),
                Opacity(element, "fill-opacity", inherited.FillOpacity), Opacity(element, "stroke-opacity", inherited.StrokeOpacity),
                (string?)element.Attribute("fill-rule") switch
                {
                    null => inherited.FillRule, "nonzero" => FillRule.NonZero, "evenodd" => FillRule.EvenOdd,
                    _ => throw new InvalidDataException("Unsupported SVG fill rule."),
                });
            if (paint.StrokeWidth < 0) throw new InvalidDataException("Negative SVG stroke width.");
            var fill = Brush(paint.Fill, paint.FillOpacity);
            var stroke = Brush(paint.Stroke, paint.StrokeOpacity);
            var pen = stroke is null || paint.StrokeWidth == 0 ? null : new Pen(stroke, paint.StrokeWidth);
            using var opacity = context.PushOpacity(Opacity(element, "opacity", 1));
            var localTransform = Transform((string?)element.Attribute("transform"));
            var combinedTransform = localTransform * inheritedTransform;
            CheckTransform(combinedTransform);
            using var transform = context.PushTransform(localTransform);
            switch (element.Name.LocalName)
            {
                case "rect":
                    var rectangle = new Rect(Length(element, "x"), Length(element, "y"), Positive(element, "width"), Positive(element, "height"));
                    var rx = Positive(element, "rx", Length(element, "ry"));
                    var ry = Positive(element, "ry", rx);
                    context.DrawRectangle(fill, pen, rectangle, Math.Min(rx, rectangle.Width / 2), Math.Min(ry, rectangle.Height / 2));
                    break;
                case "circle":
                    var radius = Positive(element, "r");
                    context.DrawEllipse(fill, pen, new Point(Length(element, "cx"), Length(element, "cy")), radius, radius);
                    break;
                case "ellipse":
                    context.DrawEllipse(fill, pen, new Point(Length(element, "cx"), Length(element, "cy")), Positive(element, "rx"), Positive(element, "ry"));
                    break;
                case "line":
                    if (pen is not null) context.DrawLine(pen, new Point(Length(element, "x1"), Length(element, "y1")), new Point(Length(element, "x2"), Length(element, "y2")));
                    break;
                case "path":
                    if (element.Attribute("d") is { Value.Length: > 0 } path)
                    {
                        var parsed = PathGeometry.Parse(path.Value);
                        parsed.FillRule = paint.FillRule;
                        context.DrawGeometry(fill, pen, parsed);
                    }
                    break;
                case "polygon": case "polyline":
                    var points = Numbers((string?)element.Attribute("points") ?? "");
                    if (points.Length % 2 != 0) throw new InvalidDataException("SVG points must contain pairs.");
                    if (points.Length < 4) break;
                    var geometry = new StreamGeometry();
                    using (var builder = geometry.Open())
                    {
                        builder.SetFillRule(paint.FillRule);
                        builder.BeginFigure(new Point(points[0], points[1]), fill is not null);
                        for (var i = 2; i < points.Length; i += 2) builder.LineTo(new Point(points[i], points[i + 1]));
                        builder.EndFigure(element.Name.LocalName == "polygon");
                    }
                    context.DrawGeometry(fill, pen, geometry);
                    break;
            }
            foreach (var child in element.Elements()) Draw(context, child, paint, combinedTransform);
        }

        private static IBrush? Brush(string value, double opacity)
        {
            if (value == "none") return null;
            if (!Color.TryParse(value, out var color)) throw new InvalidDataException("SVG requires a solid named, hex or RGB paint.");
            return new SolidColorBrush(color, opacity);
        }

        private static double Length(XElement element, string name, double fallback = 0)
        {
            if (element.Attribute(name) is not { } attribute) return fallback;
            var text = attribute.Value.Trim();
            return Number(text.EndsWith("px", StringComparison.Ordinal) ? text[..^2] : text);
        }

        private static double Positive(XElement element, string name, double fallback = 0)
        {
            var result = Length(element, name, fallback);
            return result >= 0 ? result : throw new InvalidDataException("Negative SVG dimensions are unsupported.");
        }

        private static double Opacity(XElement element, string name, double fallback)
        {
            var result = Length(element, name, fallback);
            return result is >= 0 and <= 1 ? result : throw new InvalidDataException("SVG opacity must be between zero and one.");
        }

        private static double Number(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                !double.IsFinite(number) || Math.Abs(number) > CoordinateLimit)
                throw new InvalidDataException("Invalid or oversized SVG coordinate.");
            return number;
        }

        private static double[] Numbers(string text)
        {
            var matches = NumberPattern.Matches(text);
            if (NumberPattern.Replace(text, "").Any(c => !char.IsWhiteSpace(c) && c != ','))
                throw new InvalidDataException("Malformed SVG coordinate list.");
            return matches.Select(match => Number(match.Value)).ToArray();
        }

        private static Matrix Transform(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Matrix.Identity;
            if (text.Length > 4096 || TransformPattern.Replace(text, "").Any(c => !char.IsWhiteSpace(c) && c != ','))
                throw new InvalidDataException("Malformed or oversized SVG transform.");
            var result = Matrix.Identity;
            foreach (Match match in TransformPattern.Matches(text))
            {
                var values = Numbers(match.Groups[2].Value);
                var operation = match.Groups[1].Value switch
                {
                    "matrix" when values.Length == 6 => new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]),
                    "translate" when values.Length is 1 or 2 => Matrix.CreateTranslation(values[0], values.Length == 2 ? values[1] : 0),
                    "scale" when values.Length is 1 or 2 => Matrix.CreateScale(values[0], values.Length == 2 ? values[1] : values[0]),
                    "rotate" when values.Length == 1 => Matrix.CreateRotation(values[0] * Math.PI / 180),
                    "rotate" when values.Length == 3 => Matrix.CreateRotation(values[0] * Math.PI / 180, new Point(values[1], values[2])),
                    _ => throw new InvalidDataException("Unsupported SVG transform."),
                };
                result = operation * result;
                CheckTransform(result);
            }
            return result;
        }

        private static void CheckTransform(Matrix matrix)
        {
            if (new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32 }.Any(v => !double.IsFinite(v) || Math.Abs(v) > CoordinateLimit))
                throw new InvalidDataException("SVG transform exceeds its coordinate limit.");
        }
    }
}
