using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Zyber.Views;

public sealed class ZedIcon : Control
{
    private sealed record Shape(Geometry Geometry, bool Fill, double StrokeWidth, PenLineCap Cap, PenLineJoin Join, double Opacity);
    private static readonly ConcurrentDictionary<string, Shape[]> Cache = new(StringComparer.Ordinal);
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ZedIcon, string>(nameof(Kind), "file_text_outlined");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ZedIcon, IBrush?>(nameof(Foreground), Brushes.Gray);
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static ZedIcon() => AffectsRender<ZedIcon>(KindProperty, ForegroundProperty);
    public ZedIcon() { Width = 16; Height = 16; ClipToBounds = true; }
    public override void Render(DrawingContext context)
    {
        if (Foreground is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / 16, Bounds.Height / 16));
        foreach (var shape in Cache.GetOrAdd(Kind, Load))
        {
            using var opacity = context.PushOpacity(shape.Opacity);
            var pen = shape.StrokeWidth > 0 ? new Pen(Foreground, shape.StrokeWidth, lineCap: shape.Cap, lineJoin: shape.Join) : null;
            context.DrawGeometry(shape.Fill ? Foreground : null, pen, shape.Geometry);
        }
    }
    private static Shape[] Load(string kind)
    {
        using var stream = typeof(ZedIcon).Assembly.GetManifestResourceStream("Zyber.Icons." + kind + ".svg") ?? throw new IOException("Missing bundled icon: " + kind);
        var document = XDocument.Load(stream);
        static string? Attribute(XElement element, string name) => element.AncestorsAndSelf().Select(parent => parent.Attribute(name)?.Value).FirstOrDefault(value => value is not null);
        static double Number(XElement element, string name, double fallback) => double.TryParse(Attribute(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        return document.Descendants().Where(element => element.Name.LocalName == "path" && !element.Ancestors().Any(parent => parent.Name.LocalName is "defs" or "clipPath"))
            .Select(element =>
            {
                var geometry = StreamGeometry.Parse((Attribute(element, "fill-rule") == "evenodd" ? "F0 " : "F1 ") + element.Attribute("d")!.Value);
                return new Shape(geometry, Attribute(element, "fill") != "none", Attribute(element, "stroke") is null or "none" ? 0 : Number(element, "stroke-width", 1),
                    Attribute(element, "stroke-linecap") == "round" ? PenLineCap.Round : PenLineCap.Flat,
                    Attribute(element, "stroke-linejoin") == "round" ? PenLineJoin.Round : PenLineJoin.Miter, Number(element, "opacity", 1));
            }).ToArray();
    }
}
