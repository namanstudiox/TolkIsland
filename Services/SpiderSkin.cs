using System;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace TolkIsland.Services;

/// <summary>
/// The Spider skin: a scarlet and cobalt suit palette over a deep navy shell,
/// with a fine web canopy drawn behind the island content. Everything here is
/// generated from plain shapes and colours; no external artwork is shipped.
/// </summary>
public static class SpiderSkin
{
    public const string RedDark = "#FFE23636";
    public const string RedLight = "#FFC62828";
    public const string Blue = "#FF3B5BD6";

    public const double BuddyWidth = 56;
    public const double BuddyHeight = 58;

    public static string AccentFor(SkinMode skin, bool dark, string fallback)
        => skin == SkinMode.Spider ? (dark ? RedDark : RedLight) : fallback;

    public static Brush ShellFill(bool dark) => dark
        ? Gradient("#FF0A0F1E", "#FF05070F")
        : Gradient("#FFFFF9F7", "#FFFFF4F0");

    public static Color ShellBase(bool dark)
        => dark ? Color.FromRgb(0x05, 0x07, 0x0F) : Color.FromRgb(0xFF, 0xF4, 0xF0);

    public static Brush Rim(bool dark)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        if (dark)
        {
            brush.GradientStops.Add(new GradientStop(WithAlpha(RedDark, 0xB3), 0));
            brush.GradientStops.Add(new GradientStop(WithAlpha(Blue, 0x8C), 0.55));
            brush.GradientStops.Add(new GradientStop(WithAlpha(Blue, 0xB3), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(WithAlpha(RedLight, 0x99), 0));
            brush.GradientStops.Add(new GradientStop(WithAlpha(Blue, 0x77), 0.55));
            brush.GradientStops.Add(new GradientStop(WithAlpha(Blue, 0x99), 1));
        }

        brush.Freeze();
        return brush;
    }

    public static Brush WebStroke(bool dark)
    {
        var brush = new SolidColorBrush(dark
            ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        brush.Freeze();
        return brush;
    }

    public static Brush HiddenTab(bool dark)
    {
        var red = WithAlpha(dark ? RedDark : RedLight, dark ? (byte)0xB3 : (byte)0x8C);
        var blue = WithAlpha(Blue, dark ? (byte)0xB3 : (byte)0x8C);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, red.R, red.G, red.B), 0));
        brush.GradientStops.Add(new GradientStop(red, 0.35));
        brush.GradientStops.Add(new GradientStop(blue, 0.65));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, blue.R, blue.G, blue.B), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Spokes fan from a hub above the island plus slightly sagging rings, so
    /// only the visible canopy is drawn inside the shell silhouette.
    /// </summary>
    public static Geometry BuildWeb(double width, double height)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var hub = new Point(width * 0.5, -height * 0.6);
            var reach = Math.Sqrt(width * width + height * height) * 1.05;
            const int spokes = 9;
            var first = 6 * Math.PI / 180;
            var last = Math.PI - first;
            var step = (last - first) / (spokes - 1);

            for (var i = 0; i < spokes; i++)
            {
                var angle = first + step * i;
                context.BeginFigure(hub, false, false);
                context.LineTo(
                    new Point(hub.X + Math.Cos(angle) * reach, hub.Y + Math.Sin(angle) * reach),
                    true,
                    false);
            }

            var firstRing = Math.Max(30, height * 0.26);
            var ringStep = Math.Max(30, height * 0.24);
            for (var radius = firstRing; radius < reach; radius += ringStep)
            {
                for (var i = 1; i < spokes; i++)
                {
                    var leftAngle = first + step * (i - 1);
                    var rightAngle = first + step * i;
                    var left = new Point(
                        hub.X + Math.Cos(leftAngle) * radius,
                        hub.Y + Math.Sin(leftAngle) * radius);
                    var right = new Point(
                        hub.X + Math.Cos(rightAngle) * radius,
                        hub.Y + Math.Sin(rightAngle) * radius);
                    var middle = (leftAngle + rightAngle) / 2;
                    var sag = new Point(
                        hub.X + Math.Cos(middle) * radius * 0.86,
                        hub.Y + Math.Sin(middle) * radius * 0.86);

                    context.BeginFigure(left, false, false);
                    context.QuadraticBezierTo(sag, right, true, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// The hanging mascot: a small, original spider built like a suit hero.
    /// A mask-shaped body with lens eyes and a web lattice, blue lower limbs
    /// and red suit segments. Drawn at a fixed 56 x 58 design size so the
    /// island can scale the whole image without a re-layout.
    /// </summary>
    public static DrawingImage BuildBuddy(bool dark)
    {
        var group = new DrawingGroup();

        // A transparent plate fixes the drawing bounds to the design box, so
        // the thread stays centred and the body keeps its proportions when the
        // image is stretched to the scaled mascot size.
        group.Children.Add(new GeometryDrawing(
            Brushes.Transparent,
            null,
            new RectangleGeometry(new Rect(0, 0, BuddyWidth, BuddyHeight))));

        // Two-tone silk: a dark shade under a bright core stays legible on both
        // dark and light wallpapers, unlike a single translucent stroke.
        var thread = new LineGeometry(new Point(28, 0), new Point(28, 17.2));
        group.Children.Add(new GeometryDrawing
        {
            Geometry = thread,
            Pen = RoundPen(Color.FromArgb(0x55, 0x00, 0x00, 0x00), 2.2)
        });
        group.Children.Add(new GeometryDrawing
        {
            Geometry = thread,
            Pen = RoundPen(Color.FromArgb(0xE0, 0xF5, 0xF7, 0xFA), 0.8)
        });

        // The body hangs a touch off the thread's axis: a perfect vertical
        // totem reads as a logo, a small lean reads as something alive.
        var body = new DrawingGroup
        {
            Transform = new RotateTransform(-3.2, 28, 17.2)
        };
        AddBuddyLegs(body, dark);
        AddBuddyAbdomen(body, dark);
        AddBuddyHead(body, dark);
        group.Children.Add(body);

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private readonly record struct BuddyLimb(
        Point Hip,
        Point FemurControl1,
        Point FemurControl2,
        Point Knee,
        Point TibiaControl1,
        Point TibiaControl2,
        Point Tip,
        double HipWidth,
        double KneeWidth,
        double TipWidth);

    /// <summary>
    /// Eight articulated limbs, back pairs first so the front pair reads on
    /// top. Each limb is a tapered ribbon rather than a constant stroke, which
    /// is what keeps the silhouette from looking like a stick figure.
    /// </summary>
    private static void AddBuddyLegs(DrawingGroup group, bool dark)
    {
        var limbs = new[]
        {
            new BuddyLimb(new Point(21.2, 38.2), new Point(19.0, 41.2), new Point(17.6, 43.4),
                new Point(17.0, 45.2), new Point(16.0, 48.2), new Point(14.6, 50.8),
                new Point(14.1, 53.2), 2.6, 1.8, 0.95),
            new BuddyLimb(new Point(34.8, 38.2), new Point(37.0, 41.2), new Point(38.4, 43.4),
                new Point(39.0, 45.2), new Point(40.0, 48.2), new Point(41.4, 50.8),
                new Point(41.9, 53.2), 2.6, 1.8, 0.95),
            new BuddyLimb(new Point(18.9, 35.6), new Point(15.6, 36.4), new Point(13.3, 37.6),
                new Point(12.0, 38.8), new Point(10.6, 41.2), new Point(9.8, 43.2),
                new Point(9.2, 45.2), 2.6, 1.7, 0.9),
            new BuddyLimb(new Point(37.1, 35.6), new Point(40.4, 36.4), new Point(42.7, 37.6),
                new Point(44.0, 38.8), new Point(45.4, 41.2), new Point(46.2, 43.2),
                new Point(46.8, 45.2), 2.6, 1.7, 0.9),
            new BuddyLimb(new Point(18.5, 31.8), new Point(15.2, 30.8), new Point(12.4, 30.6),
                new Point(11.0, 31.2), new Point(9.4, 33.0), new Point(7.4, 34.0),
                new Point(6.4, 35.6), 2.5, 1.6, 0.85),
            new BuddyLimb(new Point(37.5, 31.8), new Point(40.8, 30.8), new Point(43.6, 30.6),
                new Point(45.0, 31.2), new Point(46.6, 33.0), new Point(48.6, 34.0),
                new Point(49.6, 35.6), 2.5, 1.6, 0.85),
            new BuddyLimb(new Point(18.9, 27.0), new Point(15.2, 25.4), new Point(12.4, 23.6),
                new Point(11.2, 23.0), new Point(11.6, 25.8), new Point(12.8, 28.6),
                new Point(14.2, 30.4), 2.4, 1.5, 0.8),
            new BuddyLimb(new Point(37.1, 27.0), new Point(40.8, 25.2), new Point(43.4, 23.2),
                new Point(44.6, 22.4), new Point(44.4, 25.6), new Point(43.2, 28.8),
                new Point(41.8, 31.0), 2.4, 1.5, 0.8)
        };

        var femur = BuddyLimbBrush(dark, femur: true);
        var tibia = BuddyLimbBrush(dark, femur: false);
        var joint = BuddyJointBrush(dark);
        var tip = new SolidColorBrush(Hex(dark ? "#FF1B296D" : "#FF152158"));
        var edge = RoundPen(Color.FromArgb(0xA0, 0x0C, 0x07, 0x11), 0.4);
        var femurSheen = RoundPen(Color.FromArgb(0x30, 0xFF, 0xF4, 0xEC), 0.3);
        var tibiaSheen = RoundPen(Color.FromArgb(0x46, 0xC8, 0xD8, 0xFF), 0.3);
        var claw = RoundPen(Color.FromArgb(0xE0, 0x0D, 0x12, 0x28), 0.5);

        foreach (var limb in limbs)
        {
            group.Children.Add(new GeometryDrawing(
                femur, edge,
                TaperedLimb(limb.Hip, limb.FemurControl1, limb.FemurControl2, limb.Knee,
                    limb.HipWidth, limb.KneeWidth)));
            group.Children.Add(new GeometryDrawing(
                null, femurSheen,
                LimbHighlight(limb.Hip, limb.FemurControl1, limb.FemurControl2, limb.Knee,
                    limb.HipWidth, limb.KneeWidth, 0.28)));

            group.Children.Add(new GeometryDrawing(
                tibia, edge,
                TaperedLimb(limb.Knee, limb.TibiaControl1, limb.TibiaControl2, limb.Tip,
                    limb.KneeWidth * 0.96, limb.TipWidth)));
            group.Children.Add(new GeometryDrawing(
                null, tibiaSheen,
                LimbHighlight(limb.Knee, limb.TibiaControl1, limb.TibiaControl2, limb.Tip,
                    limb.KneeWidth * 0.96, limb.TipWidth, 0.24)));

            var cap = limb.KneeWidth * 0.58;
            group.Children.Add(new GeometryDrawing(
                joint,
                RoundPen(Color.FromArgb(0xB4, 0x06, 0x05, 0x12), 0.35),
                new EllipseGeometry(limb.Knee, cap, cap)));
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(0xA0, 0xE8, 0xEF, 0xFF)),
                null,
                new EllipseGeometry(
                    new Point(limb.Knee.X - 0.28, limb.Knee.Y - 0.3), 0.22, 0.2)));

            group.Children.Add(new GeometryDrawing(
                tip, null,
                new EllipseGeometry(limb.Tip, limb.TipWidth * 0.55, limb.TipWidth * 0.55)));

            var clawVector = new Point(
                limb.Tip.X - limb.TibiaControl2.X,
                limb.Tip.Y - limb.TibiaControl2.Y);
            var clawLength = Math.Sqrt(clawVector.X * clawVector.X + clawVector.Y * clawVector.Y);
            if (clawLength > 1e-6)
            {
                var clawEnd = new Point(
                    limb.Tip.X + clawVector.X / clawLength * 1.5,
                    limb.Tip.Y + clawVector.Y / clawLength * 1.5);
                var clawGeometry = new LineGeometry(limb.Tip, clawEnd);
                group.Children.Add(new GeometryDrawing(null, claw, clawGeometry));
            }
        }
    }

    private static void AddBuddyAbdomen(DrawingGroup group, bool dark)
    {
        var abdomen = BuildAbdomenPath();
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0.25, 0.1),
            EndPoint = new Point(0.8, 1)
        };
        fill.GradientStops.Add(new GradientStop(Hex(dark ? "#FF5D75DD" : "#FF4C63CC"), 0));
        fill.GradientStops.Add(new GradientStop(Hex(dark ? "#FF3550C6" : "#FF293FA8"), 0.45));
        fill.GradientStops.Add(new GradientStop(Hex(dark ? "#FF141F5C" : "#FF0E1740"), 1));
        fill.Freeze();
        group.Children.Add(new GeometryDrawing(
            fill, RoundPen(Color.FromArgb(0x8C, 0x0A, 0x06, 0x12), 0.5), abdomen));

        // The mask casts a soft occlusion onto the abdomen it sits in front of.
        var occlusion = new DrawingGroup { ClipGeometry = abdomen };
        var shade = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.22),
            GradientOrigin = new Point(0.5, 0.2),
            RadiusX = 0.62,
            RadiusY = 0.42
        };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x8A, 0x05, 0x06, 0x12), 0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x05, 0x06, 0x12), 1));
        shade.Freeze();
        occlusion.Children.Add(new GeometryDrawing(
            shade, null, new EllipseGeometry(new Point(28, 35.5), 12, 7)));
        occlusion.Freeze();
        group.Children.Add(occlusion);

        // The red chest panel follows the mask so the two masses read as one suit.
        var panel = BuildAbdomenPanelPath();
        var red = new LinearGradientBrush
        {
            StartPoint = new Point(0.2, 0),
            EndPoint = new Point(0.7, 1)
        };
        red.GradientStops.Add(new GradientStop(Hex(dark ? "#FFFF8078" : "#FFFF9A8E"), 0));
        red.GradientStops.Add(new GradientStop(Hex(dark ? "#FFE23636" : "#FFC62828"), 0.45));
        red.GradientStops.Add(new GradientStop(Hex(dark ? "#FF9A1A24" : "#FF841318"), 1));
        red.Freeze();
        group.Children.Add(new GeometryDrawing(
            red, RoundPen(Color.FromArgb(0x38, 0x1A, 0x04, 0x0A), 0.3), panel));

        group.Children.Add(WebDrawing(
            panel, new Point(28, 40.6), 6, 2.6, 2.6,
            dark ? Color.FromArgb(0x52, 0x10, 0x05, 0x0A) : Color.FromArgb(0x3C, 0x30, 0x06, 0x0A), 0.3));

        // A cool rim keeps the dark blue silhouette separated from a dark
        // wallpaper and reads as reflected light off the suit.
        var rim = new StreamGeometry();
        using (var context = rim.Open())
        {
            context.BeginFigure(new Point(32.9, 38.6), false, false);
            context.BezierTo(new Point(34.4, 41.4), new Point(34.6, 45.8), new Point(32.2, 48.8), true, false);
            context.BezierTo(new Point(31.2, 50.0), new Point(29.6, 50.8), new Point(28.4, 51.0), true, false);
        }
        rim.Freeze();
        group.Children.Add(new GeometryDrawing(
            null, RoundPen(Color.FromArgb(0x3C, 0x9F, 0xB6, 0xFF), 0.45), rim));

        var spinneret = new SolidColorBrush(Hex("#FF10131F"));
        group.Children.Add(new GeometryDrawing(
            spinneret,
            RoundPen(Color.FromArgb(0x80, 0x00, 0x00, 0x00), 0.3),
            new EllipseGeometry(new Point(28, 50.7), 1.0, 0.88)));
        var glint = new SolidColorBrush(Color.FromArgb(0xE0, 0xC8, 0xD4, 0xFF));
        group.Children.Add(new GeometryDrawing(
            glint, null, new EllipseGeometry(new Point(27.75, 50.45), 0.28, 0.22)));
    }

    private static void AddBuddyHead(DrawingGroup group, bool dark)
    {
        var head = BuildHeadPath();
        var fill = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.34, 0.26),
            Center = new Point(0.4, 0.32),
            RadiusX = 0.8,
            RadiusY = 0.9
        };
        fill.GradientStops.Add(new GradientStop(Hex(dark ? "#FFFF8C80" : "#FFFFA397"), 0));
        fill.GradientStops.Add(new GradientStop(Hex(dark ? "#FFE23636" : "#FFC62828"), 0.46));
        fill.GradientStops.Add(new GradientStop(Hex(dark ? "#FFAB2129" : "#FF951A1E"), 0.82));
        fill.GradientStops.Add(new GradientStop(Hex(dark ? "#FF75111A" : "#FF650C10"), 1));
        fill.Freeze();
        group.Children.Add(new GeometryDrawing(
            fill, RoundPen(Color.FromArgb(0x99, 0x13, 0x06, 0x0A), 0.5), head));

        var chinShade = new DrawingGroup { ClipGeometry = head };
        var shade = new RadialGradientBrush
        {
            Center = new Point(0.5, 1.08),
            GradientOrigin = new Point(0.5, 1.05),
            RadiusX = 0.62,
            RadiusY = 0.42
        };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0x08, 0x02, 0x06), 0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x08, 0x02, 0x06), 1));
        shade.Freeze();
        chinShade.Children.Add(new GeometryDrawing(
            shade, null, new RectangleGeometry(new Rect(16, 16, 24, 25))));
        chinShade.Freeze();
        group.Children.Add(chinShade);

        // A fine lattice over the red, radiating from the bridge between the
        // lenses: the suit cue the old round-eyed mascot was missing.
        group.Children.Add(WebDrawing(
            head, new Point(28, 29.0), 12, 4.6, 3.8,
            dark ? Color.FromArgb(0x5E, 0x14, 0x03, 0x08) : Color.FromArgb(0x48, 0x33, 0x05, 0x08), 0.32));

        AddBuddyEye(group, mirror: false);
        AddBuddyEye(group, mirror: true);

        var rimLight = new StreamGeometry();
        using (var context = rimLight.Open())
        {
            context.BeginFigure(new Point(20.9, 33.2), false, false);
            context.BezierTo(
                new Point(18.2, 28.6), new Point(18.6, 21.8), new Point(24.2, 19.0),
                true, false);
            context.BezierTo(
                new Point(25.4, 18.4), new Point(26.6, 18.0), new Point(27.8, 17.9),
                true, false);
        }
        rimLight.Freeze();
        group.Children.Add(new GeometryDrawing(
            null, RoundPen(Color.FromArgb(0x42, 0xFF, 0xF2, 0xEE), 0.55), rimLight));

        var bounce = new StreamGeometry();
        using (var context = bounce.Open())
        {
            context.BeginFigure(new Point(37.8, 30.8), false, false);
            context.BezierTo(new Point(36.6, 34.8), new Point(34.3, 37.4), new Point(31.6, 38.9), true, false);
        }
        bounce.Freeze();
        group.Children.Add(new GeometryDrawing(
            null, RoundPen(Color.FromArgb(0x2A, 0x9F, 0xB2, 0xFF), 0.45), bounce));

        var anchor = new EllipseGeometry(new Point(28, 17.5), 1.25, 1.0);
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Hex("#FF121525")),
            RoundPen(Color.FromArgb(0x80, 0x00, 0x00, 0x00), 0.3), anchor));
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(0xB0, 0xD6, 0xDF, 0xFF)),
            null, new EllipseGeometry(new Point(27.7, 17.2), 0.35, 0.3)));
    }

    private static void AddBuddyEye(DrawingGroup group, bool mirror)
    {
        var eye = BuildEyePath(mirror);
        var white = new LinearGradientBrush
        {
            StartPoint = new Point(0.2, 0.05),
            EndPoint = new Point(0.55, 0.95)
        };
        white.GradientStops.Add(new GradientStop(Colors.White, 0));
        white.GradientStops.Add(new GradientStop(Hex("#FFF2F6FD"), 0.5));
        white.GradientStops.Add(new GradientStop(Hex("#FFC4D2E8"), 1));
        white.Freeze();
        group.Children.Add(new GeometryDrawing(
            white, RoundPen(Color.FromArgb(0xFF, 0x08, 0x0B, 0x16), 1.55), eye));

        var tint = new DrawingGroup { ClipGeometry = eye };
        var browBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 0.45)
        };
        browBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x46, 0x08, 0x0D, 0x1C), 0));
        browBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x08, 0x0D, 0x1C), 1));
        browBrush.Freeze();
        tint.Children.Add(new GeometryDrawing(
            browBrush, null, new RectangleGeometry(new Rect(17, 22, 22, 6))));

        var tintBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.35),
            EndPoint = new Point(0, 1)
        };
        tintBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x2A, 0x4A, 0x92), 0));
        tintBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0x2A, 0x4A, 0x92), 1));
        tintBrush.Freeze();
        tint.Children.Add(new GeometryDrawing(
            tintBrush, null, new RectangleGeometry(new Rect(17, 22, 22, 11))));
        tint.Freeze();
        group.Children.Add(tint);

        var glint = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        group.Children.Add(new GeometryDrawing(
            glint, null,
            new EllipseGeometry(new Point(mirror ? 31.0 : 22.2, 25.4), 0.85, 0.48)));
        group.Children.Add(new GeometryDrawing(
            glint, null,
            new EllipseGeometry(new Point(mirror ? 32.4 : 20.8, 27.0), 0.36, 0.3)));
    }

    private static Geometry BuildHeadPath()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(28, 17.6), true, true);
            context.BezierTo(new Point(34.2, 18.0), new Point(38.5, 21.6), new Point(38.9, 26.6), true, false);
            context.BezierTo(new Point(39.3, 31.4), new Point(36.2, 36.2), new Point(31.4, 38.8), true, false);
            context.BezierTo(new Point(30.4, 39.5), new Point(29.2, 40.0), new Point(28, 40.0), true, false);
            context.BezierTo(new Point(26.8, 40.0), new Point(25.6, 39.5), new Point(24.6, 38.8), true, false);
            context.BezierTo(new Point(19.8, 36.2), new Point(16.7, 31.4), new Point(17.1, 26.6), true, false);
            context.BezierTo(new Point(17.5, 21.6), new Point(21.8, 18.0), new Point(28, 17.6), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry BuildAbdomenPath()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(28, 35.2), true, true);
            context.BezierTo(new Point(31.4, 35.4), new Point(33.9, 38.6), new Point(34.2, 42.8), true, false);
            context.BezierTo(new Point(34.5, 46.8), new Point(31.8, 49.8), new Point(28, 51.2), true, false);
            context.BezierTo(new Point(24.2, 49.8), new Point(21.5, 46.8), new Point(21.8, 42.8), true, false);
            context.BezierTo(new Point(22.1, 38.6), new Point(24.6, 35.4), new Point(28, 35.2), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry BuildAbdomenPanelPath()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(28, 36.6), true, true);
            context.BezierTo(new Point(30.0, 38.2), new Point(31.2, 41.4), new Point(31.1, 44.2), true, false);
            context.BezierTo(new Point(31.0, 47.2), new Point(29.4, 49.2), new Point(28, 50.2), true, false);
            context.BezierTo(new Point(26.6, 49.2), new Point(25.0, 47.2), new Point(24.9, 44.2), true, false);
            context.BezierTo(new Point(24.8, 41.4), new Point(26.0, 38.2), new Point(28, 36.6), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// A lens, not a circle: a wide angular eye with the pointed corner swept
    /// up and out, mirrored around the mask centre.
    /// </summary>
    private static Geometry BuildEyePath(bool mirror)
    {
        double X(double x) => mirror ? BuddyWidth - x : x;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(X(26.3), 31.6), true, true);
            context.BezierTo(new Point(X(26.4), 29.4), new Point(X(26.2), 27.4), new Point(X(25.8), 26.4), true, false);
            context.BezierTo(new Point(X(24.6), 24.9), new Point(X(22.0), 23.4), new Point(X(19.2), 23.8), true, false);
            context.BezierTo(new Point(X(20.9), 25.0), new Point(X(20.0), 27.0), new Point(X(20.3), 29.0), true, false);
            context.BezierTo(new Point(X(21.5), 29.4), new Point(X(24.0), 31.2), new Point(X(26.3), 31.6), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Builds a filled ribbon along a cubic with a linearly narrowing width, so
    /// limbs taper like a real spider leg instead of keeping a stroke's gauge.
    /// </summary>
    private static Geometry TaperedLimb(
        Point start, Point control1, Point control2, Point end,
        double startWidth, double endWidth)
    {
        const int steps = 14;
        var left = new Point[steps + 1];
        var right = new Point[steps + 1];

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var point = CubicPoint(start, control1, control2, end, t);
            var tangent = CubicTangent(start, control1, control2, end, t);
            var length = Math.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
            if (length < 1e-6)
            {
                length = 1;
            }

            var normalX = -tangent.Y / length;
            var normalY = tangent.X / length;
            var half = (startWidth + (endWidth - startWidth) * t) / 2;
            left[i] = new Point(point.X + normalX * half, point.Y + normalY * half);
            right[i] = new Point(point.X - normalX * half, point.Y - normalY * half);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(left[0], true, true);
            for (var i = 1; i <= steps; i++)
            {
                context.LineTo(left[i], true, false);
            }
            for (var i = steps; i >= 0; i--)
            {
                context.LineTo(right[i], true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// A narrow sheen that follows the upper edge of a limb, so each segment
    /// reads as a rounded volume instead of a flat silhouette.
    /// </summary>
    private static Geometry LimbHighlight(
        Point start, Point control1, Point control2, Point end,
        double startWidth, double endWidth, double inset)
    {
        const int steps = 12;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i <= steps; i++)
            {
                var t = i / (double)steps;
                var point = CubicPoint(start, control1, control2, end, t);
                var tangent = CubicTangent(start, control1, control2, end, t);
                var length = Math.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
                if (length < 1e-6)
                {
                    length = 1;
                }

                var normalX = -tangent.Y / length;
                var normalY = tangent.X / length;
                var half = (startWidth + (endWidth - startWidth) * t) / 2;
                var offset = Math.Max(0, half - inset);
                var p = new Point(point.X + normalX * offset, point.Y + normalY * offset);
                if (i == 0)
                {
                    context.BeginFigure(p, false, false);
                }
                else
                {
                    context.LineTo(p, true, false);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point CubicPoint(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        var a = u * u * u;
        var b = 3 * u * u * t;
        var c = 3 * u * t * t;
        var d = t * t * t;
        return new Point(
            a * p0.X + b * p1.X + c * p2.X + d * p3.X,
            a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    private static Point CubicTangent(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        var a = 3 * u * u;
        var b = 6 * u * t;
        var c = 3 * t * t;
        return new Point(
            a * (p1.X - p0.X) + b * (p2.X - p1.X) + c * (p3.X - p2.X),
            a * (p1.Y - p0.Y) + b * (p2.Y - p1.Y) + c * (p3.Y - p2.Y));
    }

    private static DrawingGroup WebDrawing(
        Geometry clip, Point hub, int spokes, double firstRadius, double ringStep,
        Color stroke, double thickness)
    {
        var lines = new StreamGeometry();
        using (var context = lines.Open())
        {
            const double reach = 60;
            for (var i = 0; i < spokes; i++)
            {
                var angle = -Math.PI / 2 + i * 2 * Math.PI / spokes;
                context.BeginFigure(hub, false, false);
                context.LineTo(
                    new Point(hub.X + Math.Cos(angle) * reach, hub.Y + Math.Sin(angle) * reach),
                    true, false);
            }

            var radius = firstRadius;
            for (var ring = 0; ring < 3; ring++, radius += ringStep)
            {
                for (var i = 0; i < spokes; i++)
                {
                    var leftAngle = -Math.PI / 2 + i * 2 * Math.PI / spokes;
                    var rightAngle = -Math.PI / 2 + (i + 1) * 2 * Math.PI / spokes;
                    var left = new Point(
                        hub.X + Math.Cos(leftAngle) * radius,
                        hub.Y + Math.Sin(leftAngle) * radius);
                    var right = new Point(
                        hub.X + Math.Cos(rightAngle) * radius,
                        hub.Y + Math.Sin(rightAngle) * radius);
                    var middle = (leftAngle + rightAngle) / 2;
                    var sag = new Point(
                        hub.X + Math.Cos(middle) * radius * 0.92,
                        hub.Y + Math.Sin(middle) * radius * 0.92);

                    context.BeginFigure(left, false, false);
                    context.QuadraticBezierTo(sag, right, true, false);
                }
            }
        }
        lines.Freeze();

        var group = new DrawingGroup { ClipGeometry = clip };
        group.Children.Add(new GeometryDrawing(null, RoundPen(stroke, thickness), lines));
        group.Freeze();
        return group;
    }

    private static Brush BuddyLimbBrush(bool dark, bool femur)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0),
            EndPoint = new Point(0.75, 1)
        };
        if (femur)
        {
            brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FFC24A4C" : "#FFB4403C"), 0));
            brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF8E1D24" : "#FF7E171B"), 0.55));
            brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF560C13" : "#FF4E090D"), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF839BFF" : "#FF6480EC"), 0));
            brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF3B5BD6" : "#FF314CC4"), 0.5));
            brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF1B296D" : "#FF152158"), 1));
        }
        brush.Freeze();
        return brush;
    }

    private static Brush BuddyJointBrush(bool dark)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.32, 0.28),
            Center = new Point(0.4, 0.38),
            RadiusX = 0.72,
            RadiusY = 0.72
        };
        brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF7E96F2" : "#FF6C86E8"), 0));
        brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF3149B4" : "#FF283D9E"), 0.62));
        brush.GradientStops.Add(new GradientStop(Hex(dark ? "#FF121C4C" : "#FF0F1840"), 1));
        brush.Freeze();
        return brush;
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Pen RoundPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Brush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop((Color)ColorConverter.ConvertFromString(top), 0),
                new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color WithAlpha(string hex, byte alpha)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
