// GerberEngine/CoordinateTransformer.cs
// FR-008, FR-009: mm <-> pixels according to DPI; image in Gerber coordinate system (Y up, any angle)
// to image coordinate system (top-left, Y down), perhaps (margin) so the image is not cropped.
using System;
using System.Drawing;

namespace GerberEngine
{
    public sealed class CoordinateTransformer
    {
        // bbox says (mm, he Gerber)
        private readonly RectangleD _boundsMm;
        private readonly double _marginMm;
        private readonly double _scale;          // px per mm

        public int Dpi { get; private set; }
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        public RectangleD ContentBoundsMm { get { return _boundsMm; } }

        public CoordinateTransformer(RectangleD boundsMm, int dpi, double marginMm)
        {
            if (boundsMm.IsEmpty)
                throw new ArgumentException("The bounding box is empty - there is no content to render.");
            if (dpi <= 0)
                throw new ArgumentOutOfRangeException("dpi");
            this._boundsMm = boundsMm;
            this._marginMm = marginMm;
            Dpi = dpi;
            _scale = dpi / 25.4;
            PixelWidth = Math.Max(1, (int)Math.Ceiling((boundsMm.Width + 2 * marginMm) * _scale));
            PixelHeight = Math.Max(1, (int)Math.Ceiling((boundsMm.Height + 2 * marginMm) * _scale));
        }
        /// <summary>
        /// Convert lengths from millimeters to pixels.
        /// </summary>
        /// <param name="mm"></param>
        /// <returns></returns>
        public float MmToPx(double mm) { return (float)(mm * _scale); }
        /// <summary>
        /// Gerber point (mm, Y up) -> image point (px, Y down, top-left corner).
        /// </summary>
        /// <param name="mm"></param>
        /// <returns></returns>
        public PointF ToPixel(PointD mm)
        {
            float x = (float)((mm.X - _boundsMm.MinX + _marginMm) * _scale);
            float y = (float)((_boundsMm.MaxY - mm.Y + _marginMm) * _scale);
            return new PointF(x, y);
        }
        /// <summary>
        /// Conversely: pixel points (px) -> Gerber coordinates (mm). Do not display mouse coordinates (FR-009).
        /// </summary>
        /// <param name="px"></param>
        /// <param name="py"></param>
        /// <returns></returns>
        public PointD ToMm(float px, float py)
        {
            double x = px / _scale + _boundsMm.MinX - _marginMm;
            double y = _boundsMm.MaxY + _marginMm - py / _scale;
            return new PointD(x, y);
        }
    }
}
