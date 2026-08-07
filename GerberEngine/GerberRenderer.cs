// GerberEngine/GerberRenderer.cs
// Rasterize GerberLayer using GDI+ (FR-005, FR-011, FR-014).
// Polarity strategy (FR-013): render each layer into a separate 32bppArgb bitmap, so it should be transparent;
// LPC/exposure-off to CompositingMode.SourceCopy + transparent color => EXAMINE within the layer area,
// then composite the layers on top of each other => do not excavate through the underlying layer.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GerberEngine
{
    public sealed class GerberRenderer
    {
        /// <summary>
        /// Pixel limits to avoid OutOfMemory in GDI+ (Spec 5.1.2). 
        /// ~1 GB @ 4 bytes/px.
        /// </summary>
        public const long MaxPixels = 520000000L;

        // ---------- API render ----------
        /// <summary>
        /// Rendering a layer onto a bitmap should be TRANSPARENT (for composites).
        /// </summary>
        /// <param name="layer"></param>
        /// <param name="t"></param>
        /// <param name="foreColor"></param>
        /// <returns></returns>
        public Bitmap RenderLayerTransparent(GerberLayer layer, CoordinateTransformer t, Color foreColor)
        {
            Bitmap bmp = CreateBitmap(t);
            using (Graphics g = CreateGraphics(bmp))
            {
                g.Clear(Color.Transparent);
                DrawLayer(g, layer, t, foreColor);
            }
            return bmp;
        }
        /// <summary>
        /// Render a single layer on top of a solid (output layer/binary mask, FR-011.1).
        /// </summary>
        /// <param name="layer"></param>
        /// <param name="t"></param>
        /// <param name="foreColor"></param>
        /// <param name="background"></param>
        /// <returns></returns>
        public Bitmap RenderLayerOpaque(GerberLayer layer, CoordinateTransformer t, Color foreColor, Color background)
        {
            Bitmap bmp = CreateBitmap(t);
            using (Graphics g = CreateGraphics(bmp))
            {
                g.Clear(background);
                using (Bitmap overlay = RenderLayerTransparent(layer, t, foreColor))
                    g.DrawImage(overlay, 0, 0);
            }
            return bmp;
        }
        /// <summary>
        /// Composite the visible layers in the order listed (bottom -> top) (FR-012).
        /// </summary>
        /// <param name="layers"></param>
        /// <param name="t"></param>
        /// <param name="mode"></param>
        /// <param name="background"></param>
        /// <param name="progress"></param>
        /// <returns></returns>
        public Bitmap RenderCombined(IList<GerberLayer> layers, CoordinateTransformer t, ColorMode mode,
                                     Color background, Action<int, int> progress)
        {
            Bitmap bmp = CreateBitmap(t);
            using (Graphics g = CreateGraphics(bmp))
            {
                g.Clear(background);
                int done = 0, total = CountVisible(layers);
                foreach (GerberLayer layer in layers)
                {
                    if (!layer.Visible) continue;
                    Color fore = mode == ColorMode.BinaryMask ? Color.White : layer.DisplayColor;
                    using (Bitmap layerBmp = RenderLayerTransparent(layer, t, fore))
                        g.DrawImage(layerBmp, 0, 0);
                    done++;
                    if (progress != null) progress(done, total);
                }
            }
            return bmp;
        }
        /// <summary>
        /// Default realistic color chart according to tire type (FR-011.2).
        /// </summary>
        /// <param name="type"></param>
        /// <param name="mode"></param>
        /// <returns></returns>
        public static Color DefaultColor(LayerType type, ColorMode mode)
        {
            if (mode == ColorMode.BinaryMask) return Color.White;
            switch (type)
            {
                case LayerType.TopCopper:
                case LayerType.BottomCopper:
                case LayerType.InnerCopper: return Color.FromArgb(224, 178, 82);   // dong ma vang
                case LayerType.TopSolderMask:
                case LayerType.BottomSolderMask: return Color.FromArgb(150, 0, 102, 51); // xanh solder mask ban trong
                case LayerType.TopSilkscreen:
                case LayerType.BottomSilkscreen: return Color.FromArgb(240, 240, 240);   // chu in trang
                case LayerType.BoardOutline: return Color.FromArgb(200, 200, 60);
                case LayerType.Drill: return Color.FromArgb(40, 40, 40);
                default: return Color.Gold;
            }
        }
        /// <summary>
        /// FR4+mask
        /// </summary>
        public static Color RealisticBackground { get { return Color.FromArgb(10, 45, 25); } }

        // ---------- Drawing Error ----------
        private static int CountVisible(IList<GerberLayer> layers)
        {
            int n = 0;
            foreach (GerberLayer l in layers) if (l.Visible) n++;
            return n;
        }

        private Bitmap CreateBitmap(CoordinateTransformer t)
        {
            long px = (long)t.PixelWidth * t.PixelHeight;
            if (px > MaxPixels)
                throw new InvalidOperationException(
                    "Image Size " + t.PixelWidth + "x" + t.PixelHeight +
                    " Exceed the GDI+ memory limit. Reduce the DPI or export in layers.");
            return new Bitmap(t.PixelWidth, t.PixelHeight, PixelFormat.Format32bppArgb);
        }
        private static Graphics CreateGraphics(Bitmap bmp)
        {
            Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        private void DrawLayer(Graphics g, GerberLayer layer, CoordinateTransformer t, Color fore)
        {
            foreach (GerberPrimitive prim in layer.Primitives)
            {
                bool erase = prim.Polarity == GerberPolarity.Clear;   // LPC (FR-013)
                Color col = erase ? Color.Transparent : fore;
                g.CompositingMode = erase ? CompositingMode.SourceCopy : CompositingMode.SourceOver;

                var stroke = prim as StrokePrimitive;
                if (stroke != null) { DrawStroke(g, stroke, t, col); continue; }
                var arc = prim as ArcPrimitive;
                if (arc != null) { DrawArc(g, arc, t, col); continue; }
                var flash = prim as FlashPrimitive;
                if (flash != null) { DrawFlash(g, flash, t, col, layer); continue; }
                var region = prim as RegionPrimitive;
                if (region != null) { DrawRegion(g, region, t, col); continue; }
            }
            g.CompositingMode = CompositingMode.SourceOver;
        }

        private void DrawStroke(Graphics g, StrokePrimitive s, CoordinateTransformer t, Color col)
        {
            float w = Math.Max(1f, t.MmToPx(s.Aperture.StrokeDiameter));
            using (var pen = MakePen(col, w, s.Aperture.Shape))
                g.DrawLine(pen, t.ToPixel(s.Start), t.ToPixel(s.End));
        }

        private void DrawArc(Graphics g, ArcPrimitive a, CoordinateTransformer t, Color col)
        {
            float w = Math.Max(1f, t.MmToPx(a.Aperture.StrokeDiameter));
            using (var path = BuildArcPath(a, t))
            using (var pen = MakePen(col, w, a.Aperture.Shape))
                g.DrawPath(pen, path);
        }

        private static Pen MakePen(Color col, float width, ApertureShape shape)
        {
            var pen = new Pen(col, width);
            if (shape == ApertureShape.Rectangle)
            { pen.StartCap = LineCap.Square; pen.EndCap = LineCap.Square; }
            else
            { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; }
            pen.LineJoin = LineJoin.Round;
            return pen;
        }
        /// <summary>
        /// Shift Gerber arc (Y up) to GDI+ angle (Y down):
        /// GDI angle = -Gerber angle; G03 (CCW Gerber) => negative GDI sweep, G02 => positive.        /// </summary>
        /// <param name="a"></param>
        /// <param name="t"></param>
        /// <returns></returns>
        private GraphicsPath BuildArcPath(ArcPrimitive a, CoordinateTransformer t)
        {
            PointF c = t.ToPixel(a.Center);
            float r = t.MmToPx(a.Radius);
            if (r < 0.01f) r = 0.01f;

            double a1 = Math.Atan2(a.Start.Y - a.Center.Y, a.Start.X - a.Center.X) * 180 / Math.PI;
            double a2 = Math.Atan2(a.End.Y - a.Center.Y, a.End.X - a.Center.X) * 180 / Math.PI;

            double sweep;
            bool fullCircle = Math.Abs(a.Start.X - a.End.X) < 1e-9 && Math.Abs(a.Start.Y - a.End.Y) < 1e-9;
            if (fullCircle) sweep = 360;
            else if (a.Clockwise) sweep = Normalize360(a1 - a2);
            else sweep = Normalize360(a2 - a1);

            float gdiStart = (float)(-a1);
            float gdiSweep = (float)(a.Clockwise ? sweep : -sweep);

            var path = new GraphicsPath();
            path.AddArc(c.X - r, c.Y - r, 2 * r, 2 * r, gdiStart, gdiSweep);
            return path;
        }

        private static double Normalize360(double deg)
        {
            deg = deg % 360;
            if (deg <= 0) deg += 360;
            return deg;
        }

        private void DrawFlash(Graphics g, FlashPrimitive f, CoordinateTransformer t, Color col, GerberLayer layer)
        {
            PointF pos = t.ToPixel(f.Position);
            double[] p = f.Aperture.Parameters;

            switch (f.Aperture.Shape)
            {
                case ApertureShape.Circle:
                    {
                        float d = t.MmToPx(p[0]);
                        FillEllipse(g, col, pos, d, d);
                        EraseHole(g, pos, t, p, 1);
                        break;
                    }
                case ApertureShape.Rectangle:
                    {
                        float w = t.MmToPx(p[0]), h = t.MmToPx(p[1]);
                        using (var b = new SolidBrush(col))
                            g.FillRectangle(b, pos.X - w / 2, pos.Y - h / 2, w, h);
                        EraseHole(g, pos, t, p, 2);
                        break;
                    }
                case ApertureShape.Obround:
                    {
                        float w = t.MmToPx(p[0]), h = t.MmToPx(p[1]);
                        using (var path = ObroundPath(pos, w, h))
                        using (var b = new SolidBrush(col))
                            g.FillPath(b, path);
                        EraseHole(g, pos, t, p, 2);
                        break;
                    }
                case ApertureShape.Polygon:
                    {
                        float d = t.MmToPx(p[0]);
                        int n = (int)p[1];
                        double rotDeg = p.Length > 2 ? p[2] : 0;
                        var pts = new PointF[n];
                        for (int i = 0; i < n; i++)
                        {
                            double ang = rotDeg * Math.PI / 180 + 2 * Math.PI * i / n;
                            // Y dao dau vi he anh Y xuong
                            pts[i] = new PointF(pos.X + d / 2 * (float)Math.Cos(ang),
                                                pos.Y - d / 2 * (float)Math.Sin(ang));
                        }
                        using (var b = new SolidBrush(col))
                            g.FillPolygon(b, pts);
                        EraseHole(g, pos, t, p, 3);
                        break;
                    }
                case ApertureShape.Macro:
                    DrawMacroFlash(g, f, t, col, pos, layer);
                    break;
            }
        }

        private void DrawMacroFlash(Graphics g, FlashPrimitive f, CoordinateTransformer t, Color col, PointF pos, GerberLayer layer)
        {
            List<MacroShape> shapes = ApertureMacroProcessor.Build(
                f.Aperture.Macro, f.Aperture.MacroArgs, f.Aperture.MacroUnitScale, layer.Warnings);

            float s = t.MmToPx(1.0); // px per mm
            CompositingMode outer = g.CompositingMode;
            foreach (MacroShape shape in shapes)
            {
                using (GraphicsPath path = (GraphicsPath)shape.Path.Clone())
                {
                    // mm cuc bo (Y len) -> pixel: scale X, dao Y, tinh tien ve vi tri flash
                    using (var mtx = new Matrix(s, 0, 0, -s, pos.X, pos.Y))
                        path.Transform(mtx);

                    bool eraseThis = !shape.ExposureOn || outer == CompositingMode.SourceCopy;
                    g.CompositingMode = eraseThis ? CompositingMode.SourceCopy : CompositingMode.SourceOver;
                    Color c = eraseThis ? Color.Transparent : col;
                    using (var b = new SolidBrush(c))
                        g.FillPath(b, path);
                }
                shape.Path.Dispose();
            }
            g.CompositingMode = outer;
        }

        private static void FillEllipse(Graphics g, Color col, PointF center, float w, float h)
        {
            using (var b = new SolidBrush(col))
                g.FillEllipse(b, center.X - w / 2, center.Y - h / 2, w, h);
        }
        /// <summary>
        /// Create a hole in the aperture (hole parameter at the end of AD) using a transparent SourceCopy.
        /// </summary>
        /// <param name="g"></param>
        /// <param name="pos"></param>
        /// <param name="t"></param>
        /// <param name="p"></param>
        /// <param name="holeIndex"></param>
        private static void EraseHole(Graphics g, PointF pos, CoordinateTransformer t, double[] p, int holeIndex)
        {
            if (p.Length <= holeIndex || p[holeIndex] <= 0) return;
            float hd = t.MmToPx(p[holeIndex]);
            CompositingMode prev = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            FillEllipse(g, Color.Transparent, pos, hd, hd);
            g.CompositingMode = prev;
        }

        private static GraphicsPath ObroundPath(PointF c, float w, float h)
        {
            var path = new GraphicsPath();
            float x = c.X - w / 2, y = c.Y - h / 2;
            float r = Math.Min(w, h) / 2;
            // Capsule: rounded-rect voi ban kinh = nua canh ngan
            path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
            path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
            path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
            path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            path.CloseFigure();
            return path;
        }
        /// <summary>
        /// G36/G37 polygon pour: fill Winding, contour can be flat (FR-014).
        /// </summary>
        /// <param name="g"></param>
        /// <param name="region"></param>
        /// <param name="t"></param>
        /// <param name="col"></param>
        private void DrawRegion(Graphics g, RegionPrimitive region, CoordinateTransformer t, Color col)
        {
            using (var path = new GraphicsPath(FillMode.Winding))
            {
                foreach (RegionContour contour in region.Contours)
                {
                    path.StartFigure();
                    PointD cur = contour.Start;
                    foreach (RegionSegment seg in contour.Segments)
                    {
                        if (seg.IsArc)
                        {
                            var arc = new ArcPrimitive
                            {
                                Start = cur,
                                End = seg.End,
                                Center = seg.Center,
                                Clockwise = seg.Clockwise,
                                Aperture = new Aperture { Shape = ApertureShape.Circle, Parameters = new double[] { 0 } }
                            };
                            using (GraphicsPath ap = BuildArcPath(arc, t))
                                if (ap.PointCount > 0) path.AddPath(ap, true); // connect = true noi lien contour
                        }
                        else
                        {
                            path.AddLine(t.ToPixel(cur), t.ToPixel(seg.End));
                        }
                        cur = seg.End;
                    }
                    path.CloseFigure();
                }
                using (var b = new SolidBrush(col))
                    g.FillPath(b, path);
            }
        }
    }
}
