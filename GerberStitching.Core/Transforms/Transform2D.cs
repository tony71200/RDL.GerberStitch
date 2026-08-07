using System;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Transforms
{
    /// <summary>Immutable canonical 2D transform backed by a finite double[3,3] matrix.</summary>
    public sealed class Transform2D
    {
        private readonly double[,] _matrix;

        public Transform2D(double[,] matrix)
        {
            _matrix = ValidateAndClone(matrix);
        }

        public double[,] Matrix { get { return ToArray(); } }
        public double this[int row, int column] { get { return _matrix[row, column]; } }

        public static Transform2D Identity { get { return new Transform2D(new[,] { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } }); } }
        public static Transform2D Translation(double x, double y) { return new Transform2D(new[,] { { 1d, 0d, x }, { 0d, 1d, y }, { 0d, 0d, 1d } }); }

        public double[,] ToArray()
        {
            return ValidateAndClone(_matrix);
        }

        /// <summary>Returns this * other for column vectors; other is applied first.</summary>
        public Transform2D Multiply(Transform2D other)
        {
            if (other == null) throw new ArgumentNullException("other");
            return new Transform2D(Multiply(_matrix, other._matrix));
        }

        public Transform2D Invert()
        {
            var m = _matrix;
            var a = m[0, 0]; var b = m[0, 1]; var c = m[0, 2];
            var d = m[1, 0]; var e = m[1, 1]; var f = m[1, 2];
            var g = m[2, 0]; var h = m[2, 1]; var i = m[2, 2];
            var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Transform is singular and cannot be inverted.");
            var inv = new double[3, 3];
            inv[0, 0] = (e * i - f * h) / det;
            inv[0, 1] = (c * h - b * i) / det;
            inv[0, 2] = (b * f - c * e) / det;
            inv[1, 0] = (f * g - d * i) / det;
            inv[1, 1] = (a * i - c * g) / det;
            inv[1, 2] = (c * d - a * f) / det;
            inv[2, 0] = (d * h - e * g) / det;
            inv[2, 1] = (b * g - a * h) / det;
            inv[2, 2] = (a * e - b * d) / det;
            return new Transform2D(inv);
        }

        public Mat ToMatCv64FCopy()
        {
            return ToMatCv64FCopy(_matrix);
        }

        public HTuple ToHalconHomMat2DCopy()
        {
            return ToHalconHomMat2DCopy(_matrix);
        }

        public static Transform2D FromMatCv64F(Mat source)
        {
            return new Transform2D(FromMatCv64FCopy(source));
        }

        public static Transform2D FromHalconHomMat2D(HTuple homMat2D)
        {
            return new Transform2D(FromHalconHomMat2DCopy(homMat2D));
        }

        public static Mat ToMatCv64FCopy(double[,] matrix)
        {
            var clone = ValidateAndClone(matrix);
            var mat = new Mat(3, 3, MatType.CV_64FC1);
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    mat.Set<double>(row, col, clone[row, col]);
            return mat;
        }

        public static double[,] FromMatCv64FCopy(Mat source)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (source.Rows != 3 || source.Cols != 3 || source.Type() != MatType.CV_64FC1) throw new ArgumentException("Transform Mat must be 3x3 CV_64F.", "source");
            var result = new double[3, 3];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    result[row, col] = source.At<double>(row, col);
            return result;
        }

        public static HTuple ToHalconHomMat2DCopy(double[,] matrix)
        {
            var clone = ValidateAndClone(matrix);
            if (Math.Abs(clone[2, 0]) > 1e-12 || Math.Abs(clone[2, 1]) > 1e-12 || Math.Abs(clone[2, 2] - 1d) > 1e-12)
                throw new ArgumentException("HALCON HomMat2D conversion supports affine 2D transforms with bottom row [0, 0, 1].", "matrix");
            return new HTuple(new[] { clone[0, 0], clone[0, 1], clone[0, 2], clone[1, 0], clone[1, 1], clone[1, 2] });
        }

        public static double[,] FromHalconHomMat2DCopy(HTuple homMat2D)
        {
            if (homMat2D == null) throw new ArgumentNullException("homMat2D");
            if (homMat2D.Length != 6) throw new ArgumentException("HALCON HomMat2D tuple must contain 6 affine values.", "homMat2D");
            return new[,] { { homMat2D[0].D, homMat2D[1].D, homMat2D[2].D }, { homMat2D[3].D, homMat2D[4].D, homMat2D[5].D }, { 0d, 0d, 1d } };
        }

        private static double[,] ValidateAndClone(double[,] matrix)
        {
            if (matrix == null) throw new ArgumentNullException("matrix");
            if (matrix.GetLength(0) != 3 || matrix.GetLength(1) != 3) throw new ArgumentException("Transform matrix must be double[3,3].", "matrix");
            var clone = new double[3, 3];
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    var value = matrix[row, col];
                    if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Transform matrix contains a non-finite value.", "matrix");
                    clone[row, col] = value;
                }
            }
            return clone;
        }

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var result = new double[3, 3];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    for (int k = 0; k < 3; k++)
                        result[row, col] += a[row, k] * b[k, col];
            return result;
        }
    }
}
