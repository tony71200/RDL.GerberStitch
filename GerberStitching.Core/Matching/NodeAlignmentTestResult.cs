using System;
using System.Drawing;

namespace GerberViewer.Stitching.Matching
{
    public sealed class NodeAlignmentTestResult : IDisposable
    {
        public MatchResult Match { get; set; }
        public string FormattedMatch { get; set; }
        public Bitmap SamplePreview { get; set; }
        public Bitmap TestPreview { get; set; }
        public Bitmap TransformedCapturedPreview { get; set; }
        public Bitmap OverlayPreview { get; set; }
        public string MaskSource { get; set; }

        public void Dispose()
        {
            if (SamplePreview != null) 
            { 
                SamplePreview.Dispose(); 
                SamplePreview = null; 
            }
            if (TestPreview != null)
            {
                TestPreview.Dispose();
                TestPreview = null;
            }
            if (TransformedCapturedPreview != null) 
            { 
                TransformedCapturedPreview.Dispose(); 
                TransformedCapturedPreview = null; 
            }
            if (OverlayPreview != null) 
            { 
                OverlayPreview.Dispose(); 
                OverlayPreview = null; 
            }
        }
    }
}
