using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace NxeDashboard.Runtime
{
    public sealed class XuiSceneRenderer
    {
        private readonly Dictionary<string, XElement> visuals =
            new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        public int VisualCount => visuals.Count;

        public async Task InitializeAsync(string skinPath)
        {
            var document = await LoadDocumentAsync(skinPath);
            foreach (var candidate in document.Root.Elements())
            {
                if (candidate.Name.LocalName != "XuiVisual") continue;
                var id = Property(candidate, "Id");
                if (!string.IsNullOrWhiteSpace(id)) visuals[id] = candidate;
            }
        }

        public async Task<FrameworkElement> RenderSceneAsync(string scenePath, XuiBindings bindings)
        {
            var document = await LoadDocumentAsync(scenePath);
            var section = scenePath.Split('/')[0];
            var canvasElement = document.Root;
            var root = new Canvas
            {
                Width = Number(canvasElement, "Width", 1280),
                Height = Number(canvasElement, "Height", 720)
            };

            foreach (var child in canvasElement.Elements().Where(IsRenderable))
            {
                root.Children.Add(await RenderElementAsync(child, section, bindings, 0));
            }
            return root;
        }

        private async Task<FrameworkElement> RenderElementAsync(
            XElement source, string section, XuiBindings bindings, int visualDepth)
        {
            var type = source.Name.LocalName;
            var id = Property(source, "Id") ?? string.Empty;
            var referencedVisual = Property(source, "Visual") ?? string.Empty;

            // These elements are render-target/compositor passes in the original
            // XUI runtime. They are not ordinary static pictures. Rendering a
            // single animation frame or a radial cover as a XAML rectangle is
            // what produced the oversized white and black panels on Windows.
            if (id.StartsWith("animation", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("BottomCover", StringComparison.OrdinalIgnoreCase) ||
                referencedVisual.StartsWith("defaultSlotAnimation", StringComparison.OrdinalIgnoreCase))
            {
                return new Canvas { Visibility = Visibility.Collapsed };
            }

            FrameworkElement result;

            if (type == "XuiFigure")
            {
                result = await CreateFigureAsync(source, section);
            }
            else if (type == "XuiImage" || type == "XuiImagePresenter" || type == "XuiNineGrid")
            {
                // XUI uses alpha-only nine-grids as render-target masks. Drawing
                // CornerMask.png as ordinary colour content creates the large
                // white rectangles seen in the first Windows proof build.
                result = type == "XuiNineGrid" && Integer(source, "ColorWriteFlags", 0) == 8
                    ? new Canvas()
                    : await CreateImageAsync(source, section, bindings);
            }
            else if (type == "XuiText" || type == "XuiTextPresenter")
            {
                result = CreateText(source, bindings);
            }
            else
            {
                result = new Canvas();
            }

            ApplyCommonProperties(result, source);

            var container = result as Panel;
            var visualName = referencedVisual;
            if (container != null && visualDepth < 4 && !string.IsNullOrWhiteSpace(visualName) && visuals.TryGetValue(visualName, out var visual))
            {
                foreach (var visualChild in visual.Elements().Where(IsRenderable))
                {
                    container.Children.Add(await RenderElementAsync(visualChild, "dashuisk", bindings, visualDepth + 1));
                }
            }

            if (container != null)
            {
                foreach (var child in source.Elements().Where(IsRenderable))
                {
                    container.Children.Add(await RenderElementAsync(child, section, bindings, visualDepth));
                }
            }

            return result;
        }

        private async Task<FrameworkElement> CreateImageAsync(XElement source, string section, XuiBindings bindings)
        {
            var path = Property(source, "ImagePath") ?? Property(source, "TextureFileName");
            var association = Integer(source, "DataAssociation", 0);
            if (string.IsNullOrWhiteSpace(path) && bindings.Images.TryGetValue(association, out var bound)) path = bound;

            if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".xur", StringComparison.OrdinalIgnoreCase))
            {
                var referenced = ResolveXuiReference(section, path);
                return await RenderSceneAsync(referenced, bindings);
            }

            var image = new Image { Stretch = Stretch.Fill };
            if (!string.IsNullOrWhiteSpace(path))
            {
                image.Source = new BitmapImage(new Uri(ResolveAssetUri(section, path)));
            }
            return image;
        }

        private async Task<FrameworkElement> CreateFigureAsync(XElement source, string section)
        {
            var rectangle = new Windows.UI.Xaml.Shapes.Rectangle();
            var fill = source.Element("Properties")?.Element("Fill");
            var fillProperties = fill?.Element("Properties");
            var texture = fillProperties?.Element("TextureFileName")?.Value;
            if (!string.IsNullOrWhiteSpace(texture))
            {
                rectangle.Fill = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri(ResolveAssetUri(section, texture))),
                    Stretch = Stretch.Fill
                };
            }
            else
            {
                var gradient = fillProperties?.Element("Gradient")?.Element("Properties");
                var stops = gradient == null
                    ? new List<GradientStop>()
                    : gradient.Elements("StopColor")
                        .Select(color => new
                        {
                            Index = (int?)color.Attribute("index") ?? 0,
                            Color = ParseColor(color.Value, Colors.Transparent)
                        })
                        .Join(
                            gradient.Elements("StopPos").Select(position => new
                            {
                                Index = (int?)position.Attribute("index") ?? 0,
                                Offset = ParseDouble(position.Value, 0)
                            }),
                            color => color.Index,
                            position => position.Index,
                            (color, position) => new GradientStop
                            {
                                Color = color.Color,
                                Offset = Math.Max(0, Math.Min(1, position.Offset))
                            })
                        .OrderBy(stop => stop.Offset)
                        .ToList();

                if (stops.Count > 0)
                {
                    var brush = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0.5),
                        EndPoint = new Point(1, 0.5)
                    };
                    foreach (var stop in stops) brush.GradientStops.Add(stop);
                    rectangle.Fill = brush;
                }
                else
                {
                    var fillColor = fillProperties?.Element("FillColor")?.Value;
                    // An unspecified fill is transparent in XUI. The old opaque
                    // fallback caused the large black blobs over retail slots.
                    rectangle.Fill = new SolidColorBrush(ParseColor(fillColor, Colors.Transparent));
                }
            }
            await Task.CompletedTask;
            return rectangle;
        }

        private FrameworkElement CreateText(XElement source, XuiBindings bindings)
        {
            var id = Property(source, "Id") ?? string.Empty;
            var association = Integer(source, "DataAssociation", -1);
            var text = Property(source, "Text") ?? string.Empty;
            if (association >= 0 && bindings.Text.TryGetValue(association, out var associated)) text = associated;
            if (bindings.TextByElement.TryGetValue(id, out var named)) text = named;

            return new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = Number(source, "PointSize", 18),
                Foreground = new SolidColorBrush(ParseColor(Property(source, "TextColor"), Colors.White)),
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static void ApplyCommonProperties(FrameworkElement target, XElement source)
        {
            target.Width = Number(source, "Width", double.NaN);
            target.Height = Number(source, "Height", double.NaN);
            target.Opacity = Number(source, "Opacity", 1);
            target.Visibility = Boolean(source, "Show", true) ? Visibility.Visible : Visibility.Collapsed;

            var position = Vector(source, "Position", 0, 0, 0);
            Canvas.SetLeft(target, position[0]);
            Canvas.SetTop(target, position[1]);

            var scale = Vector(source, "Scale", 1, 1, 1);
            var rotation = Vector(source, "Rotation", 0, 0, 0, 1);
            var angle = 2 * Math.Atan2(rotation[2], rotation[3]) * 180 / Math.PI;
            target.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform { ScaleX = scale[0], ScaleY = scale[1] },
                    new RotateTransform { Angle = angle }
                }
            };
        }

        private static bool IsRenderable(XElement element)
        {
            var name = element.Name.LocalName;
            return name.StartsWith("Xui", StringComparison.Ordinal) &&
                   name != "XuiCanvas" && name != "XuiVisual" && name != "XuiShader";
        }

        private static string Property(XElement element, string name) =>
            element.Element("Properties")?.Element(name)?.Value;

        private static double Number(XElement element, string name, double fallback)
        {
            return ParseDouble(Property(element, name), fallback);
        }

        private static double ParseDouble(string value, double fallback) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number : fallback;

        private static int Integer(XElement element, string name, int fallback) =>
            int.TryParse(Property(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

        private static bool Boolean(XElement element, string name, bool fallback) =>
            bool.TryParse(Property(element, name), out var value) ? value : fallback;

        private static double[] Vector(XElement element, string name, params double[] fallback)
        {
            var value = Property(element, name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var values = value.Split(',').Select(x =>
                double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0).ToArray();
            return values.Length >= fallback.Length ? values : fallback;
        }

        private static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            value = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value;
            if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return fallback;
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        private static string ResolveXuiReference(string section, string path)
        {
            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("controlpack://", StringComparison.OrdinalIgnoreCase))
                return "controlp/" + Path.ChangeExtension(normalized.Substring(14), ".xui");
            return section + "/" + Path.ChangeExtension(normalized, ".xui");
        }

        private static string ResolveAssetUri(string section, string path)
        {
            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("common://", StringComparison.OrdinalIgnoreCase))
                normalized = "dashcomm/" + normalized.Substring(9);
            else if (normalized.StartsWith("sharedres://", StringComparison.OrdinalIgnoreCase))
                normalized = "sharedres/" + normalized.Substring(12);
            else if (normalized.StartsWith("controlpack://", StringComparison.OrdinalIgnoreCase))
                normalized = "controlp/" + normalized.Substring(14);
            else if (!normalized.Contains("://") && !normalized.Contains('/'))
                normalized = section + "/" + normalized;
            return "ms-appx:///RetailNXE/extracted/" + normalized;
        }

        private static async Task<XDocument> LoadDocumentAsync(string relativePath)
        {
            var uri = new Uri("ms-appx:///RetailNXE/xui/" + relativePath.Replace('\\', '/'));
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            using (var stream = await file.OpenStreamForReadAsync())
            {
                return XDocument.Load(stream);
            }
        }
    }
}
