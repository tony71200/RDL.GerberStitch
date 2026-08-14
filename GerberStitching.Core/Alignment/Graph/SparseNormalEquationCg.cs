using System;

namespace GerberViewer.Stitching.Alignment.Graph
{
    // [Tony] [Change time: 2026-08-04] [Purpose: Matrix-free Conjugate Gradient over the sparse normal equations (J^T
    // W J + eI) x = -J^T W r, avoiding a dense n x n matrix for pose-graph sizes up to a few thousand tiles.]
    public sealed class SparseNormalEquationCg
    {
        public static double[] Solve(int unknownCount, Action<Action<double, double, int[], double[]>> enumerateRows,
                                     int maxIterations, double tolerance)
        {
            var b = new double[unknownCount];
            var diag = new double[unknownCount];
            enumerateRows(delegate(double weight, double residual, int[] cols, double[] grad) {
                var w2 = weight * weight;
                for (var i = 0; i < cols.Length; i++)
                {
                    b[cols[i]] -= w2 * residual * grad[i];
                    diag[cols[i]] += w2 * grad[i] * grad[i];
                }
            });

            var trace = 0.0;
            for (var i = 0; i < unknownCount; i++)
                trace += diag[i];
            var epsilon = unknownCount > 0 ? 1e-9 * (trace / unknownCount) : 1e-9;
            if (epsilon <= 0)
                epsilon = 1e-12;

            Func<double[], double[]> matVec = delegate(double[] x)
            {
                var result = new double[unknownCount];
                enumerateRows(delegate(double weight, double residual, int[] cols, double[] grad) {
                    var dot = 0.0;
                    for (var i = 0; i < cols.Length; i++)
                        dot += grad[i] * x[cols[i]];
                    var w2 = weight * weight;
                    for (var i = 0; i < cols.Length; i++)
                        result[cols[i]] += w2 * dot * grad[i];
                });
                for (var i = 0; i < unknownCount; i++)
                    result[i] += epsilon * x[i];
                return result;
            };

            var minv = new double[unknownCount];
            for (var i = 0; i < unknownCount; i++)
                minv[i] = 1.0 / Math.Max(diag[i] + epsilon, 1e-12);

            var xVec = new double[unknownCount];
            var r = (double[])b.Clone();
            var r0Norm = Norm(r);
            if (r0Norm < 1e-15)
                return xVec;

            var z = new double[unknownCount];
            for (var i = 0; i < unknownCount; i++)
                z[i] = minv[i] * r[i];
            var p = (double[])z.Clone();
            var rz = Dot(r, z);

            for (var iter = 0; iter < maxIterations; iter++)
            {
                var ap = matVec(p);
                var pAp = Dot(p, ap);
                if (Math.Abs(pAp) < 1e-300)
                    break;
                var alpha = rz / pAp;
                for (var i = 0; i < unknownCount; i++)
                {
                    xVec[i] += alpha * p[i];
                    r[i] -= alpha * ap[i];
                }
                var rNorm = Norm(r);
                if (rNorm <= tolerance * r0Norm)
                    break;
                for (var i = 0; i < unknownCount; i++)
                    z[i] = minv[i] * r[i];
                var rzNew = Dot(r, z);
                var beta = rzNew / rz;
                for (var i = 0; i < unknownCount; i++)
                    p[i] = z[i] + beta * p[i];
                rz = rzNew;
            }
            return xVec;
        }

        private static double Dot(double[] a, double[] b)
        {
            var sum = 0.0;
            for (var i = 0; i < a.Length; i++)
                sum += a[i] * b[i];
            return sum;
        }

        private static double Norm(double[] a)
        {
            return Math.Sqrt(Dot(a, a));
        }
    }
}
