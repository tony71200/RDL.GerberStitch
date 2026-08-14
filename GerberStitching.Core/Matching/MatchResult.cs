using System;
using System.Collections.Generic;
using System.Globalization;
using GerberViewer.Stitching.Transforms;

namespace GerberViewer.Stitching.Matching
{
    public sealed class MatchResult
    {
        public bool Success { get; set; }
        public Transform2D MovingToReferenceTransform { get; set; }
        public double TranslationX { get; set; }
        public double TranslationY { get; set; }
        public double RotationDeg { get; set; }
        public double Scale { get; set; } = 1.0;
        public double RawScore { get; set; }
        public double NormalizedConfidence { get; set; }
        public double OverlapRatio { get; set; }
        public string MatcherName { get; set; }
        public MatcherKind MatcherKind { get; set; }
        public MatchFailureReason FailureReason { get; set; }
        public string FailureMessage { get; set; }
        public string Warning { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public IDictionary<string, string> Diagnostics { get; private set; } = new Dictionary<string, string>();

        public static MatchResult Failed(string matcherName, MatchFailureReason reason, string message)
        {
            return new MatchResult { MatcherName = matcherName,
                                     FailureReason = reason,
                                     FailureMessage = message,
                                     RawScore = double.NaN,
                                     NormalizedConfidence = 0d,
                                     OverlapRatio = 0d,
                                     Scale = 1d };
        }

        public override string ToString()
        {
            var text = string.Format(CultureInfo.InvariantCulture,
                                     "MatcherName={0}; Success={1}; \tScore={2:F5}; Dx={3:F4}; Dy={4:F4}; Angle={5:F5}",
                                     OneLine(MatcherName), Success, RawScore, TranslationX, TranslationY, RotationDeg);

            if (!Success || !IsFinite(RawScore) || !IsFinite(TranslationX) || !IsFinite(TranslationY) ||
                !IsFinite(RotationDeg))
            {
                text += string.Format(CultureInfo.InvariantCulture, "; FailureReason={0}; FailureMessage={1}",
                                      FailureReason, OneLine(FailureMessage));
            }

            return text;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string OneLine(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        }
    }
}
