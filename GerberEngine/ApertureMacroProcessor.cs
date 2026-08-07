// GerberEngine/ApertureMacroProcessor.cs
// FR-006: Uses Aperture Macro (AM) shape. Supports primitives 1, 4, 5, 20, 21;
// Primitives 6/7 degrade safety; error -> warning, no exception thrown during rendering.
// Output: GraphicsPath in LOCAL COORDINATES mm (angle = aperture center, UPWARD DIRECTION);
// GerberRenderer is responsible for scaling/flipping/translating to pixels.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace GerberEngine
{
    public sealed class MacroShape
    {
        public GraphicsPath Path;     // mm, cuc bo, Y len
        public bool ExposureOn;       // false = khoet (erase)
    }

    public static class ApertureMacroProcessor
    {
        public static List<MacroShape> Build(ApertureMacro macro, double[] args, double unitScale, List<string> warnings)
        {
            var shapes = new List<MacroShape>();
            var vars = new Dictionary<int, double>();
            for (int i = 0; i < args.Length; i++)
                vars[i + 1] = args[i];

            foreach (string rawBlock in macro.Blocks)
            {
                string block = rawBlock.Trim();
                if (block.Length == 0) continue;
                try
                {
                    if (block.StartsWith("0")) continue; // comment primitive
                    if (block.StartsWith("$"))
                    {
                        // The value of $3 is $3 = $1 + $2 x 0.5
                        int eq = block.IndexOf('=');
                        int id = int.Parse(block.Substring(1, eq - 1), CultureInfo.InvariantCulture);
                        vars[id] = Eval(block.Substring(eq + 1), vars);
                        continue;
                    }

                    string[] parts = block.Split(',');
                    int code = (int)Eval(parts[0], vars);
                    double[] m = new double[parts.Length - 1];
                    for (int i = 1; i < parts.Length; i++)
                        m[i - 1] = Eval(parts[i], vars);

                    switch (code)
                    {
                        case 1: shapes.Add(Circle(m, unitScale)); break;
                        case 4: shapes.Add(Outline(m, unitScale)); break;
                        case 5: shapes.Add(Polygon(m, unitScale)); break;
                        case 20: shapes.Add(VectorLine(m, unitScale)); break;
                        case 21: shapes.Add(CenterLine(m, unitScale)); break;

                        case 6: // Moire - decline: outer circle
                            warnings.Add("Macro '" + macro.Name + "': primitive 6 (moire) suy giam ve circle");
                            shapes.Add(ApproxCircle(m[0], m[1], m[2], unitScale, true));
                            break;
                        case 7: // Thermal - deterioration: dry rim (outer on, inner off)
                            warnings.Add("Macro '" + macro.Name + "': primitive 7 (thermal) suy giam ve vanh khan");
                            shapes.Add(ApproxCircle(m[0], m[1], m[2], unitScale, true));
                            shapes.Add(ApproxCircle(m[0], m[1], m[3], unitScale, false));
                            break;
                        default:
                            warnings.Add("Macro '" + macro.Name + "': primitive " + code + " chua ho tro - bo qua");
                            break;
                    }
                }
                catch (Exception e)
                {
                    warnings.Add("Macro '" + macro.Name + "' block error (" + block + "): " + e.Message);
                }
            }
            return shapes;
        }

        // ---------- Primitive builders (return local mm path) ----------

        private static MacroShape Circle(double[] m, double s)
        {
            // exposure, dia, cx, cy [,rot]
            bool on = m[0] > 0.5;
            float d = (float)(m[1] * s), cx = (float)(m[2] * s), cy = (float)(m[3] * s);
            var p = new GraphicsPath();
            p.AddEllipse(cx - d / 2, cy - d / 2, d, d);
            Rotate(p, m.Length > 4 ? m[4] : 0);
            return new MacroShape { Path = p, ExposureOn = on };
        }

        private static MacroShape Outline(double[] m, double s)
        {
            // exposure, n, x0,y0, x1,y1 ... xn,yn, rot  (n+1 cap toa do, diem cuoi trung diem dau)
            bool on = m[0] > 0.5;
            int n = (int)m[1];
            var pts = new PointF[n + 1];
            for (int i = 0; i <= n; i++)
                pts[i] = new PointF((float)(m[2 + i * 2] * s), (float)(m[3 + i * 2] * s));
            var p = new GraphicsPath(FillMode.Winding);
            p.AddPolygon(pts);
            Rotate(p, m.Length > 2 + (n + 1) * 2 ? m[2 + (n + 1) * 2] : 0);
            return new MacroShape { Path = p, ExposureOn = on };
        }

        private static MacroShape Polygon(double[] m, double s)
        {
            // exposure, n, cx, cy, dia, rot
            bool on = m[0] > 0.5;
            int n = (int)m[1];
            double cx = m[2] * s, cy = m[3] * s, r = m[4] * s / 2;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                pts[i] = new PointF((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
            }
            var p = new GraphicsPath(FillMode.Winding);
            p.AddPolygon(pts);
            Rotate(p, m.Length > 5 ? m[5] : 0);
            return new MacroShape { Path = p, ExposureOn = on };
        }

        private static MacroShape VectorLine(double[] m, double s)
        {
            // exposure, width, x1,y1, x2,y2, rot -> hinh chu nhat theo huong vector
            bool on = m[0] > 0.5;
            double w = m[1] * s / 2;
            double x1 = m[2] * s, y1 = m[3] * s, x2 = m[4] * s, y2 = m[5] * s;
            double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { dx = 1; dy = 0; len = 1; }
            double nx = -dy / len * w, ny = dx / len * w; // phap tuyen
            var pts = new[]
            {
                new PointF((float)(x1 + nx), (float)(y1 + ny)),
                new PointF((float)(x2 + nx), (float)(y2 + ny)),
                new PointF((float)(x2 - nx), (float)(y2 - ny)),
                new PointF((float)(x1 - nx), (float)(y1 - ny))
            };
            var p = new GraphicsPath(FillMode.Winding);
            p.AddPolygon(pts);
            Rotate(p, m.Length > 6 ? m[6] : 0);
            return new MacroShape { Path = p, ExposureOn = on };
        }

        private static MacroShape CenterLine(double[] m, double s)
        {
            // exposure, width, height, cx, cy, rot
            bool on = m[0] > 0.5;
            float w = (float)(m[1] * s), h = (float)(m[2] * s), cx = (float)(m[3] * s), cy = (float)(m[4] * s);
            var p = new GraphicsPath(FillMode.Winding);
            p.AddRectangle(new RectangleF(cx - w / 2, cy - h / 2, w, h));
            Rotate(p, m.Length > 5 ? m[5] : 0);
            return new MacroShape { Path = p, ExposureOn = on };
        }

        private static MacroShape ApproxCircle(double cx, double cy, double dia, double s, bool on)
        {
            float d = (float)(dia * s);
            var p = new GraphicsPath();
            p.AddEllipse((float)(cx * s) - d / 2, (float)(cy * s) - d / 2, d, d);
            return new MacroShape { Path = p, ExposureOn = on };
        }

        /// <summary>Rotate around MACRO OC (0,0) according to Gerber standard, unit of measurement.</summary>
        private static void Rotate(GraphicsPath p, double degrees)
        {
            if (Math.Abs(degrees) < 1e-9) return;
            using (var mtx = new Matrix())
            {
                mtx.Rotate((float)degrees);
                p.Transform(mtx);
            }
        }

        // ---------- Macro expression evaluation tool: so, $n, + - x X / and parentheses ----------

        private static double Eval(string expr, Dictionary<int, double> vars)
        {
            int pos = 0;
            double v = EvalSum(expr, ref pos, vars);
            return v;
        }

        private static double EvalSum(string e, ref int p, Dictionary<int, double> vars)
        {
            double v = EvalProduct(e, ref p, vars);
            while (p < e.Length)
            {
                char c = e[p];
                if (c == '+') { p++; v += EvalProduct(e, ref p, vars); }
                else if (c == '-') { p++; v -= EvalProduct(e, ref p, vars); }
                else break;
            }
            return v;
        }

        private static double EvalProduct(string e, ref int p, Dictionary<int, double> vars)
        {
            double v = EvalAtom(e, ref p, vars);
            while (p < e.Length)
            {
                char c = e[p];
                if (c == 'x' || c == 'X') { p++; v *= EvalAtom(e, ref p, vars); }
                else if (c == '/') { p++; v /= EvalAtom(e, ref p, vars); }
                else break;
            }
            return v;
        }

        private static double EvalAtom(string e, ref int p, Dictionary<int, double> vars)
        {
            while (p < e.Length && e[p] == ' ') p++;
            if (p >= e.Length) return 0;

            if (e[p] == '(')
            {
                p++;
                double v = EvalSum(e, ref p, vars);
                if (p < e.Length && e[p] == ')') p++;
                return v;
            }
            if (e[p] == '-') { p++; return -EvalAtom(e, ref p, vars); }
            if (e[p] == '+') { p++; return EvalAtom(e, ref p, vars); }
            if (e[p] == '$')
            {
                p++;
                int start = p;
                while (p < e.Length && char.IsDigit(e[p])) p++;
                int id = int.Parse(e.Substring(start, p - start), CultureInfo.InvariantCulture);
                double v;
                return vars.TryGetValue(id, out v) ? v : 0; // bien chua gan = 0 theo chuan
            }
            int s0 = p;
            while (p < e.Length && (char.IsDigit(e[p]) || e[p] == '.')) p++;
            return double.Parse(e.Substring(s0, p - s0), CultureInfo.InvariantCulture);
        }
    }
}
