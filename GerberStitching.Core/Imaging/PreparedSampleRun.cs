using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using GerberViewer.Stitching.Configuration;
using HalconDotNet;

namespace GerberViewer.Stitching.Imaging
{
    public sealed class ImagePreprocessMetadata
    {
        public SamplePreprocessMode Mode { get; set; }
        public bool KeepAspectRatio { get; set; }
        public bool Inverted { get; set; }
        public string CoordinateSpace { get { return "processed-source pixels"; } }
    }

    public sealed class PreparedSampleRun : IDisposable
    {
        public HObject SourceImage { get; private set; }
        public HObject ProcessedImage { get; private set; }
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public int ProcessedWidth { get; private set; }
        public int ProcessedHeight { get; private set; }
        public GerberSampleConfig ConfigSnapshot { get; private set; }
        public SampleGridLayout Layout { get; private set; }
        public IReadOnlyList<SampleTileLayout> TilesByOrder { get; private set; }
        public ImagePreprocessMetadata PreprocessMetadata { get; private set; }

        public PreparedSampleRun(HObject sourceImage, HObject processedImage, int sourceWidth, int sourceHeight, int processedWidth, int processedHeight, GerberSampleConfig configSnapshot, SampleGridLayout layout, ImagePreprocessMetadata metadata)
        {
            if (sourceImage == null || !sourceImage.IsInitialized()) 
                throw new ArgumentException("Owned source image is required.", nameof(sourceImage));
            if (processedImage == null || !processedImage.IsInitialized()) 
                throw new ArgumentException("Owned processed image is required.", nameof(processedImage));
            if (sourceWidth <= 0 || sourceHeight <= 0 || processedWidth <= 0 || processedHeight <= 0) 
                throw new ArgumentOutOfRangeException("Image dimensions must be positive.");
            if (configSnapshot == null) 
                throw new ArgumentNullException(nameof(configSnapshot));
            if (layout == null || layout.Tiles == null) 
                throw new ArgumentNullException(nameof(layout));
            SourceImage = sourceImage; 
            ProcessedImage = processedImage; 
            SourceWidth = sourceWidth; 
            SourceHeight = sourceHeight; 
            ProcessedWidth = processedWidth; 
            ProcessedHeight = processedHeight;
            ConfigSnapshot = configSnapshot; 
            Layout = layout; 
            TilesByOrder = layout.Tiles.OrderBy(t => t.OrderIndex).ToList().AsReadOnly(); 
            PreprocessMetadata = metadata ?? new ImagePreprocessMetadata();
        }

        public void Dispose()
        {
            if (SourceImage != null && SourceImage.IsInitialized()) SourceImage.Dispose();
            if (ProcessedImage != null && ProcessedImage.IsInitialized()) ProcessedImage.Dispose();
            SourceImage = null; ProcessedImage = null;
        }
    }

    public interface ISamplePreparationService { 
        PreparedSampleRun Prepare(HObject sourceImage, GerberSampleConfig config, CancellationToken cancellationToken); 
    }

    public sealed class SamplePreparationService : ISamplePreparationService
    {
        public PreparedSampleRun Prepare(HObject sourceImage, GerberSampleConfig config, CancellationToken cancellationToken)
        {
            if (sourceImage == null || !sourceImage.IsInitialized()) 
                throw new ArgumentException("Source image is required.", nameof(sourceImage));
            if (config == null) 
                throw new ArgumentNullException(nameof(config));
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = CloneConfig(config);
            HObject ownedSource = null; HObject processed = null;
            try
            {
                HOperatorSet.CopyImage(sourceImage, out ownedSource);
                var sourceSize = GetSize(ownedSource);
                var validation = GerberSampleConfigValidator.Validate(snapshot, sourceSize);
                if (!validation.IsValid) 
                    throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
                processed = Preprocess(
                    ownedSource, 
                    sourceSize, 
                    snapshot, 
                    cancellationToken);
                var processedSize = GetSize(processed);
                var layout = SampleGeometryCalculator.Calculate(processedSize.Width, processedSize.Height, snapshot);
                return new PreparedSampleRun(ownedSource, processed, sourceSize.Width, sourceSize.Height, processedSize.Width, processedSize.Height, snapshot, layout, new ImagePreprocessMetadata { Mode = snapshot.PreprocessMode, KeepAspectRatio = snapshot.KeepAspectRatio, Inverted = snapshot.InvertImage });
            }
            catch
            {
                if (ownedSource != null && ownedSource.IsInitialized()) 
                    ownedSource.Dispose();
                if (processed != null && processed.IsInitialized()) 
                    processed.Dispose();
                throw;
            }
        }

        private static HObject Preprocess(HObject source, Size sourceSize, GerberSampleConfig config, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            HObject result = null;
            var width = sourceSize.Width;
            var height = sourceSize.Height;
            if (config.PreprocessMode == SamplePreprocessMode.None || 
                (width == sourceSize.Width && height == sourceSize.Height)) 
                HOperatorSet.CopyImage(source, out result);
            else HOperatorSet.ZoomImageSize(source, out result, width, height, "constant");
            if (config.InvertImage)
            {
                HObject inverted = null;
                try 
                { 
                    HOperatorSet.InvertImage(result, out inverted); 
                    result.Dispose(); 
                    result = inverted; 
                    inverted = null; 
                }
                finally { 
                    if (inverted != null && inverted.IsInitialized()) 
                        inverted.Dispose(); 
                }
            }
            return result;
        }

        private static Size GetSize(HObject image)
        {
            HTuple w = null, h = null;
            try 
            { 
                HOperatorSet.GetImageSize(image, out w, out h); 
                return new Size(w.I, h.I); 
            }
            finally 
            { 
                if (w != null) 
                    w.Dispose(); 
                if (h != null) 
                    h.Dispose(); 
            }
        }

        private static GerberSampleConfig CloneConfig(GerberSampleConfig c)
        {
            return new GerberSampleConfig 
            { 
                SourceRasterPath = c.SourceRasterPath, 
                OutputDirectory = c.OutputDirectory, 
                Rows = c.Rows, 
                Columns = c.Columns, 
                CropOrder = c.CropOrder, 
                StartOrder = c.StartOrder, 
                InvertImage = c.InvertImage, 
                OverlapValue = c.OverlapValue, 
                OverlapUnit = c.OverlapUnit, 
                PreprocessMode = c.PreprocessMode, 
                KeepAspectRatio = c.KeepAspectRatio, 
                OutputFormat = c.OutputFormat, 
                TileNamePattern = c.TileNamePattern, 
                ProcessedWidth = c.ProcessedWidth, 
                ProcessedHeight = c.ProcessedHeight, 
                PadColor = c.PadColor 
            };
        }
    }
}
