// GerberEngine/GerberParser.cs
// Parser RS-274X / Gerber X2 (FR-001, FR-002, FR-007, NFR-003).
// Output: GerberLayer with primitives that adjust the height and angle of Gerber (Y len).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GerberEngine
{
    public sealed class GerberParser
    {
        private const double InchToMm = 25.4;
        // parser status
        private GerberUnit _unit = GerberUnit.Millimeter;
        private int _intDigits = 3, _decDigits = 6; // FSLA default
        private GerberPolarity _polarity = GerberPolarity.Dark;
        private int _interpolation = 1;               // 1=G01 linear, 2=G02 CW, 3=G03 CCW
        private bool _inRegion;
        private PointD _current;
        private Aperture _currentAperture;
        private readonly Dictionary<int, Aperture> _apertures = new Dictionary<int, Aperture>();
        private readonly Dictionary<string, ApertureMacro> _macros = new Dictionary<string, ApertureMacro>(StringComparer.OrdinalIgnoreCase);
        private RegionPrimitive _region;
        private RegionContour _contour;
        private GerberLayer _layer;
        private int _lineNo;
        /// <summary>
        /// Parse a Gerber file into a GerberLayer. Do not throw an exception for the singular syntax error (NFR-003).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public GerberLayer ParseFile(string path)
        {
            _layer = new GerberLayer { FilePath = path, FileName = Path.GetFileName(path) };
            string content = File.ReadAllText(path);
            Parse(content);
            if (_layer.Type == LayerType.Unknown)
                _layer.Type = LayerTypeDetector.DetectFromFileName(_layer.FileName);
            return _layer;
        }

        private void Parse(string content)
        {
            int i = 0, n = content.Length;
            var word = new StringBuilder();
            _lineNo = 1;

            while (i < n)
            {
                char c = content[i];
                if (c == '\n') { _lineNo++; i++; continue; }
                if (c == '\r' || c == ' ' || c == '\t') { i++; continue; }

                if (c == '%')
                {
                    // Extended command: doc den '%' dong
                    int end = content.IndexOf('%', i + 1);
                    if (end < 0) { Warn("Thieu '%' dong extended command"); break; }
                    string block = content.Substring(i + 1, end - i - 1);
                    CountLines(block);
                    SafeExec(() => HandleExtended(block));
                    i = end + 1;
                    continue;
                }

                // Word command: doc den '*'
                int star = content.IndexOf('*', i);
                if (star < 0) break;
                string wordCmd = content.Substring(i, star - i).Trim();
                CountLines(content.Substring(i, star - i));
                if (wordCmd.Length > 0) SafeExec(() => HandleWord(wordCmd));
                i = star + 1;
            }
        }

        private void CountLines(string s)
        {
            foreach (char c in s) if (c == '\n') _lineNo++;
        }

        private void SafeExec(Action a)
        {
            try { a(); }
            catch (Exception ex) { Warn("Bo qua lenh loi: " + ex.Message); }
        }

        private void Warn(string msg)
        {
            _layer.Warnings.Add("Dong " + _lineNo + ": " + msg);
        }

        // ---------- Extended commands (%...%) ----------
        private void HandleExtended(string block)
        {
            // Mot block co the chua nhieu lenh phan cach '*'
            string[] cmds = block.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in cmds)
            {
                string cmd = raw.Trim().TrimStart('\r', '\n');
                if (cmd.Length < 2) continue;

                if (cmd.StartsWith("FS")) ParseFormat(cmd);
                else if (cmd.StartsWith("MO")) ParseUnit(cmd);
                else if (cmd.StartsWith("ADD")) ParseApertureDef(cmd);
                else if (cmd.StartsWith("AM")) { ParseMacro(cmd, cmds); return; } // AM nuot cac block sau trong cung %..%
                else if (cmd.StartsWith("LPD")) _polarity = GerberPolarity.Dark;
                else if (cmd.StartsWith("LPC")) _polarity = GerberPolarity.Clear;
                else if (cmd.StartsWith("TF.FileFunction")) ParseFileFunction(cmd);
                else if (cmd.StartsWith("TF") || cmd.StartsWith("TA") || cmd.StartsWith("TO") || cmd.StartsWith("TD")) { /* X2 attribute khac: bo qua */ }
                else if (cmd.StartsWith("IP") || cmd.StartsWith("LN") || cmd.StartsWith("SR") || cmd.StartsWith("OF") || cmd.StartsWith("AS") || cmd.StartsWith("IN") || cmd.StartsWith("MI") || cmd.StartsWith("SF")) { /* deprecated/khong ho tro */ }
                else Warn("Extended command khong nhan dien: " + cmd.Substring(0, Math.Min(10, cmd.Length)));
            }
        }

        private void ParseFormat(string cmd)
        {
            // FSLAX36Y36 -> 3 nguyen, 6 thap phan (omit leading zeros, absolute)
            int ix = cmd.IndexOf('X');
            if (ix < 0 || ix + 2 >= cmd.Length) { Warn("FS khong hop le"); return; }
            _intDigits = cmd[ix + 1] - '0';
            _decDigits = cmd[ix + 2] - '0';
        }

        private void ParseUnit(string cmd)
        {
            if (cmd.Contains("IN")) _unit = GerberUnit.Inch;
            else _unit = GerberUnit.Millimeter;
            _layer.SourceUnit = _unit;
        }

        private void ParseFileFunction(string cmd)
        {
            // %TF.FileFunction,Copper,L1,Top*% ...
            _layer.Type = LayerTypeDetector.DetectFromX2(cmd);
        }

        private void ParseApertureDef(string cmd)
        {
            // ADD10C,0.1524  |  ADD11R,1.5X0.8  |  ADD12P,1X6X0.5  |  ADD13MYMACRO,0.5X0.1
            int p = 3;
            int codeStart = p;
            while (p < cmd.Length && char.IsDigit(cmd[p])) p++;
            int code = int.Parse(cmd.Substring(codeStart, p - codeStart), CultureInfo.InvariantCulture);

            int comma = cmd.IndexOf(',', p);
            string shapeName = comma < 0 ? cmd.Substring(p) : cmd.Substring(p, comma - p);
            double[] args = new double[0];
            if (comma >= 0)
            {
                string[] parts = cmd.Substring(comma + 1).Split('X');
                args = new double[parts.Length];
                for (int k = 0; k < parts.Length; k++)
                    args[k] = double.Parse(parts[k], CultureInfo.InvariantCulture);
            }

            var ap = new Aperture { Code = code };
            switch (shapeName)
            {
                case "C": ap.Shape = ApertureShape.Circle; ap.Parameters = ToMmArray(args, -1); break;
                case "R": ap.Shape = ApertureShape.Rectangle; ap.Parameters = ToMmArray(args, -1); break;
                case "O": ap.Shape = ApertureShape.Obround; ap.Parameters = ToMmArray(args, -1); break;
                case "P":
                    ap.Shape = ApertureShape.Polygon;
                    // P: dia(mm), soCanh(khong doi), rotation deg(khong doi), hole(mm)
                    ap.Parameters = (double[])args.Clone();
                    if (args.Length > 0) ap.Parameters[0] = ToMm(args[0]);
                    if (args.Length > 3) ap.Parameters[3] = ToMm(args[3]);
                    break;
                default:
                    ApertureMacro macro;
                    if (_macros.TryGetValue(shapeName, out macro))
                    {
                        ap.Shape = ApertureShape.Macro;
                        ap.Macro = macro;
                        ap.MacroArgs = (double[])args.Clone();          // giu don vi file
                        ap.MacroUnitScale = _unit == GerberUnit.Inch ? InchToMm : 1.0;
                    }
                    else
                    {
                        Warn("Aperture macro not yet defined: " + shapeName + " -> thay bang Circle 0.1mm");
                        ap.Shape = ApertureShape.Circle;
                        ap.Parameters = new double[] { 0.1 };
                    }
                    break;
            }
            _apertures[code] = ap;
        }

        private void ParseMacro(string firstCmd, string[] allCmds)
        {
            // %AMNAME*block1*block2*...*%  - firstCmd la "AMNAME", cac block la phan tu sau trong allCmds
            var macro = new ApertureMacro { Name = firstCmd.Substring(2).Trim() };
            bool after = false;
            foreach (string cmd in allCmds)
            {
                if (!after) { if (ReferenceEquals(cmd, firstCmd) || cmd.Trim() == firstCmd) after = true; continue; }
                macro.Blocks.Add(cmd.Trim());
            }
            _macros[macro.Name] = macro;
        }

        // ---------- Word commands (...*) ----------

        private void HandleWord(string cmd)
        {
            // Co the co G-code dinh kem toa do: "G01X100Y200D01"
            if (cmd.StartsWith("G04")) return;                       // comment
            if (cmd == "M02" || cmd == "M00" || cmd == "M01") return; // end of file
            if (cmd == "G36") { BeginRegion(); return; }
            if (cmd == "G37") { EndRegion(); return; }
            if (cmd == "G74" || cmd == "G75") return;                // quadrant mode: luon xu ly multi-quadrant
            if (cmd == "G70") { _unit = GerberUnit.Inch; return; }   // deprecated
            if (cmd == "G71") { _unit = GerberUnit.Millimeter; return; }
            if (cmd == "G90" || cmd == "G91") return;                // absolute/incremental (chi ho tro absolute)

            int i = 0;
            // G-codes command
            while (i < cmd.Length && cmd[i] == 'G')
            {
                int j = i + 1;
                while (j < cmd.Length && char.IsDigit(cmd[j])) j++;
                int g = int.Parse(cmd.Substring(i + 1, j - i - 1), CultureInfo.InvariantCulture);
                if (g == 1) _interpolation = 1;
                else if (g == 2) _interpolation = 2;
                else if (g == 3) _interpolation = 3;
                else if (g == 54) { /* select aperture prefix - tiep tuc doc Dnn */ }
                i = j;
            }
            if (i >= cmd.Length) return;

            // "Dnn" does not include coordinates: nn>=10 la chon aperture; D01/D02/D03 tran thao tac tai diem hien hanh
            if (cmd[i] == 'D' && !ContainsCoord(cmd, i))
            {
                int code = int.Parse(cmd.Substring(i + 1), CultureInfo.InvariantCulture);
                if (code >= 10) SelectAperture(code);
                else if (code == 1) Interpolate(_current, 0, 0);
                else if (code == 2) MoveTo(_current);
                else if (code == 3) Flash(_current);
                return;
            }

            // Coordinate command + D01/D02/D03
            double? x = null, y = null, iOfs = null, jOfs = null;
            int dCode = -1;
            while (i < cmd.Length)
            {
                char key = cmd[i];
                int j = i + 1;
                while (j < cmd.Length && (char.IsDigit(cmd[j]) || cmd[j] == '+' || cmd[j] == '-')) j++;
                string num = cmd.Substring(i + 1, j - i - 1);
                switch (key)
                {
                    case 'X': x = ParseCoord(num); break;
                    case 'Y': y = ParseCoord(num); break;
                    case 'I': iOfs = ParseCoord(num); break;
                    case 'J': jOfs = ParseCoord(num); break;
                    case 'D': dCode = int.Parse(num, CultureInfo.InvariantCulture); break;
                    default: Warn("Ky tu khong nhan dien '" + key + "' trong: " + cmd); j = i + 1; break;
                }
                i = j;
            }

            var target = new PointD(x ?? _current.X, y ?? _current.Y);

            if (dCode >= 10) { SelectAperture(dCode); return; }
            switch (dCode)
            {
                case 1: Interpolate(target, iOfs ?? 0, jOfs ?? 0); break;
                case 2: MoveTo(target); break;
                case 3: Flash(target); break;
                case -1:
                    // Khong co D-code: theo chuan cu, lap lai thao tac truoc do -> coi nhu D01
                    Interpolate(target, iOfs ?? 0, jOfs ?? 0);
                    break;
            }
        }

        private static bool ContainsCoord(string cmd, int from)
        {
            for (int k = from; k < cmd.Length; k++)
                if (cmd[k] == 'X' || cmd[k] == 'Y' || cmd[k] == 'I' || cmd[k] == 'J') return true;
            return false;
        }

        private double ParseCoord(string num)
        {
            // Omit leading zeros (normalization): value = int / 10^decDigits
            if (string.IsNullOrEmpty(num)) return 0;
            long v = long.Parse(num, CultureInfo.InvariantCulture);
            double val = v / Math.Pow(10, _decDigits);
            return ToMm(val);
        }

        private double ToMm(double v) { return _unit == GerberUnit.Inch ? v * InchToMm : v; }

        private double[] ToMmArray(double[] a, int skipIndex)
        {
            var r = new double[a.Length];
            for (int k = 0; k < a.Length; k++) r[k] = k == skipIndex ? a[k] : ToMm(a[k]);
            return r;
        }

        private void SelectAperture(int code)
        {
            if (!_apertures.TryGetValue(code, out _currentAperture))
            {
                Warn("Aperture D" + code + " chua dinh nghia");
                _currentAperture = new Aperture { Code = code, Shape = ApertureShape.Circle, Parameters = new double[] { 0.1 } };
                _apertures[code] = _currentAperture;
            }
        }

        // ---------- Drawing operations ----------
        private void MoveTo(PointD p)
        {
            if (_inRegion) CloseContourAndStartNew(p);
            _current = p;
        }

        private void Interpolate(PointD target, double iOfs, double jOfs)
        {
            if (_inRegion)
            {
                if (_contour == null) StartContour(_current);
                var seg = new RegionSegment { End = target };
                if (_interpolation == 2 || _interpolation == 3)
                {
                    seg.IsArc = true;
                    seg.Center = new PointD(_current.X + iOfs, _current.Y + jOfs);
                    seg.Clockwise = _interpolation == 2;
                }
                _contour.Segments.Add(seg);
            }
            else
            {
                if (_currentAperture == null) { Warn("D01 truoc khi chon aperture"); SelectAperture(10); }
                if (_interpolation == 1)
                {
                    _layer.Primitives.Add(new StrokePrimitive
                    {
                        Start = _current,
                        End = target,
                        Aperture = _currentAperture,
                        Polarity = _polarity
                    });
                }
                else
                {
                    _layer.Primitives.Add(new ArcPrimitive
                    {
                        Start = _current,
                        End = target,
                        Center = new PointD(_current.X + iOfs, _current.Y + jOfs),
                        Clockwise = _interpolation == 2,
                        Aperture = _currentAperture,
                        Polarity = _polarity
                    });
                }
            }
            _current = target;
        }

        private void Flash(PointD p)
        {
            if (_currentAperture == null) { Warn("D03 before selecting aperture"); SelectAperture(10); }
            _layer.Primitives.Add(new FlashPrimitive { Position = p, Aperture = _currentAperture, Polarity = _polarity });
            _current = p;
        }

        private void BeginRegion()
        {
            _inRegion = true;
            _region = new RegionPrimitive { Polarity = _polarity };
            _contour = null;
        }

        private void StartContour(PointD start)
        {
            _contour = new RegionContour { Start = start };
            _region.Contours.Add(_contour);
        }

        private void CloseContourAndStartNew(PointD newStart)
        {
            _contour = null; // contour finished; A new contour begins when D01 follows.
        }

        private void EndRegion()
        {
            _inRegion = false;
            if (_region != null && _region.Contours.Count > 0)
                _layer.Primitives.Add(_region);
            _region = null;
            _contour = null;
        }
    }
    /// <summary>
    /// Recognize the X2 FileFunction file type or file name (FR-002).
    /// </summary>
    public static class LayerTypeDetector
    {
        public static LayerType DetectFromX2(string tf)
        {
            string s = tf.ToUpperInvariant();
            if (s.Contains("COPPER")) return s.Contains("BOT") ? LayerType.BottomCopper
                                       : s.Contains("TOP") ? LayerType.TopCopper : LayerType.InnerCopper;
            if (s.Contains("SOLDERMASK")) return s.Contains("BOT") ? LayerType.BottomSolderMask : LayerType.TopSolderMask;
            if (s.Contains("LEGEND") || s.Contains("SILK")) return s.Contains("BOT") ? LayerType.BottomSilkscreen : LayerType.TopSilkscreen;
            if (s.Contains("PROFILE") || s.Contains("OUTLINE")) return LayerType.BoardOutline;
            if (s.Contains("DRILL") || s.Contains("PLATED")) return LayerType.Drill;
            return LayerType.Unknown;
        }

        public static LayerType DetectFromFileName(string fileName)
        {
            string f = fileName.ToLowerInvariant();
            string ext = System.IO.Path.GetExtension(f);
            switch (ext)
            {
                case ".gtl": return LayerType.TopCopper;
                case ".gbl": return LayerType.BottomCopper;
                case ".gts": return LayerType.TopSolderMask;
                case ".gbs": return LayerType.BottomSolderMask;
                case ".gto": return LayerType.TopSilkscreen;
                case ".gbo": return LayerType.BottomSilkscreen;
                case ".gko": case ".gm1": case ".gml": return LayerType.BoardOutline;
                case ".drl": case ".xln": return LayerType.Drill;
            }
            if (f.Contains("top") && f.Contains("copper")) return LayerType.TopCopper;
            if (f.Contains("bottom") && f.Contains("copper")) return LayerType.BottomCopper;
            if (f.Contains("mask")) return f.Contains("bot") ? LayerType.BottomSolderMask : LayerType.TopSolderMask;
            if (f.Contains("silk") || f.Contains("legend")) return f.Contains("bot") ? LayerType.BottomSilkscreen : LayerType.TopSilkscreen;
            if (f.Contains("outline") || f.Contains("edge") || f.Contains("profile")) return LayerType.BoardOutline;
            if (f.Contains("drill")) return LayerType.Drill;
            return LayerType.Unknown;
        }
    }
}
