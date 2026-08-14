using System;
using GerberViewer.Stitching.Transforms;

namespace GerberViewer.Stitching.Matching.Halcon
{
    // [Tony] [Change time: 2026-07-27] [Purpose: Keep NCC/Shape pose conversion in one canonical
    // MovingImage-to-ReferenceImage utility.]
    public static class HalconModelPoseConverter
    {
        public static Transform2D ToMovingToReference(double foundColumn, double foundRow, double angleRad,
                                                      double referenceOriginColumn, double referenceOriginRow)
        {
            return ToMovingToReference(foundColumn, foundRow, angleRad, 1.0, referenceOriginColumn, referenceOriginRow);
        }

        public static Transform2D ToMovingToReference(double foundColumn, double foundRow, double angleRad,
                                                      double scale, double referenceOriginColumn,
                                                      double referenceOriginRow)
        {
            var c = Math.Cos(angleRad);
            var s = Math.Sin(angleRad);
            var tx = foundColumn - scale * c * referenceOriginColumn + scale * s * referenceOriginRow;
            var ty = foundRow - scale * s * referenceOriginColumn - scale * c * referenceOriginRow;
            return new Transform2D(
                       new[,] { { scale * c, -scale * s, tx }, { scale * s, scale * c, ty }, { 0d, 0d, 1d } })
                .Invert();
        }

        public static string Format(Transform2D transform)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "[{0:R},{1:R},{2:R};{3:R},{4:R},{5:R};0,0,1]", transform[0, 0], transform[0, 1],
                                 transform[0, 2], transform[1, 0], transform[1, 1], transform[1, 2]);
        }
    }
}
