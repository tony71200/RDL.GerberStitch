using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using GerberViewer.Stitching.Configuration;

namespace GerberViewer.Stitching.Models
{
    [DataContract]
    public sealed class SampleManifest
    {
        public const int CurrentVersion = 2;
        [DataMember(Order = 1)] public int ManifestVersion { get; set; }
        [DataMember(Order = 2)] public string RootDirectory { get; set; }
        [DataMember(Order = 3)] public string SourceRasterPath { get; set; }
        [DataMember(Order = 4)] public int SourceWidth { get; set; }
        [DataMember(Order = 5)] public int SourceHeight { get; set; }
        [DataMember(Order = 6)] public int ProcessedWidth { get; set; }
        [DataMember(Order = 7)] public int ProcessedHeight { get; set; }
        [DataMember(Order = 8)] public string CropOrder { get; set; }
        [DataMember(Order = 9)] public string StartOrder { get; set; }
        [DataMember(Order = 10)] public List<SampleTileInfo> Tiles { get; set; }
        [DataMember(Order = 11)] public DateTime CreatedUtc { get; set; }
        [DataMember(Order = 12, EmitDefaultValue = false)] public string ProcessedSamplePath { get; set; }
        [DataMember(Order = 13, EmitDefaultValue = false)] public double[][] SourceToProcessedTransform { get; set; }
        [DataMember(Order = 14, EmitDefaultValue = false)] public string PreprocessMode { get; set; }
        [DataMember(Order = 15)] public int ProcessedChannelCount { get; set; }
        [DataMember(Order = 16)] public int ProcessedBitDepth { get; set; }
        // [Claude] [Change time: 2026-08-07] [Purpose: Record the model generation mode so Worker knows whether the manifest includes .ncm/.shm files or requires runtime model creation.]
        // Values: "OnTheFly" or "Pregenerate". Older manifests omit this field; treat null as OnTheFly.
        [DataMember(Order = 17, EmitDefaultValue = false)] public string ModelGeneration { get; set; }
    }

    [DataContract]
    public sealed class SampleTileInfo
    {
        [DataMember(Order = 1)] public int OrderIndex { get; set; }
        [DataMember(Order = 2)] public int Row { get; set; }
        [DataMember(Order = 3)] public int Column { get; set; }
        [DataMember(Order = 4)] public string ExpectedPath { get; set; }
        [DataMember(Order = 5)] public int ExpectedX { get; set; }
        [DataMember(Order = 6)] public int ExpectedY { get; set; }
        [DataMember(Order = 7)] public int Width { get; set; }
        [DataMember(Order = 8)] public int Height { get; set; }
        [DataMember(Order = 9, EmitDefaultValue = false)] public string NccModelPath { get; set; }
        [DataMember(Order = 10, EmitDefaultValue = false)] public int? NccModelRoiX { get; set; }
        [DataMember(Order = 11, EmitDefaultValue = false)] public int? NccModelRoiY { get; set; }
        [DataMember(Order = 12, EmitDefaultValue = false)] public int? NccModelRoiWidth { get; set; }
        [DataMember(Order = 13, EmitDefaultValue = false)] public int? NccModelRoiHeight { get; set; }
        [DataMember(Order = 14, EmitDefaultValue = false)] public double? NccReferenceOriginRow { get; set; }
        [DataMember(Order = 15, EmitDefaultValue = false)] public double? NccReferenceOriginColumn { get; set; }
        [DataMember(Order = 16)] public int NccModelSchemaVersion { get; set; }
        [DataMember(Order = 17, EmitDefaultValue = false)] public string SampleContentClass { get; set; }
        [DataMember(Order = 18)] public double SampleMinGray { get; set; }
        [DataMember(Order = 19)] public double SampleMaxGray { get; set; }
        [DataMember(Order = 20)] public double SampleMeanGray { get; set; }
        [DataMember(Order = 21)] public double SampleStdDevGray { get; set; }
        [DataMember(Order = 22)] public double SampleNonZeroPixelRatio { get; set; }
        [DataMember(Order = 23, EmitDefaultValue = false)] public string ShapeModelPath { get; set; }
        [DataMember(Order = 24, EmitDefaultValue = false)] public int? ShapeModelRoiX { get; set; }
        [DataMember(Order = 25, EmitDefaultValue = false)] public int? ShapeModelRoiY { get; set; }
        [DataMember(Order = 26, EmitDefaultValue = false)] public int? ShapeModelRoiWidth { get; set; }
        [DataMember(Order = 27, EmitDefaultValue = false)] public int? ShapeModelRoiHeight { get; set; }
        [DataMember(Order = 28, EmitDefaultValue = false)] public double? ShapeReferenceOriginRow { get; set; }
        [DataMember(Order = 29, EmitDefaultValue = false)] public double? ShapeReferenceOriginColumn { get; set; }
        [DataMember(Order = 30)] public int ShapeModelSchemaVersion { get; set; }
    }

    public sealed class SampleManifestValidationResult
    {
        public List<string> Errors { get; private set; }
        public bool IsValid { get { return Errors.Count == 0; } }
        public SampleManifestValidationResult() { Errors = new List<string>(); }
    }

    public static class SampleManifestSerializer
    {
        public static void WriteValidated(string path, SampleManifest manifest, bool requireFiles)
        {
            var result = SampleManifestValidator.Validate(manifest, requireFiles);
            if (!result.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors));
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) 
                Directory.CreateDirectory(dir);
            IndentedJson.WriteDataContract(path, manifest, typeof(SampleManifest));
            var readback = Read(path);
            var readbackResult = SampleManifestValidator.Validate(readback, requireFiles);
            if (!readbackResult.IsValid) 
                throw new InvalidOperationException("Manifest readback failed: " + string.Join(Environment.NewLine, readbackResult.Errors));
        }

        public static SampleManifest Read(string path)
        {
            using (var stream = File.OpenRead(path)) 
                return (SampleManifest)new DataContractJsonSerializer(typeof(SampleManifest)).ReadObject(stream);
        }
    }

    public static class SampleManifestValidator
    {
        public static SampleManifestValidationResult Validate(SampleManifest manifest, bool requireFiles)
        {
            var result = new SampleManifestValidationResult();
            if (manifest == null) 
            { 
                result.Errors.Add("Manifest is null."); 
                return result; 
            }
            if (manifest.ManifestVersion <= 0) 
                result.Errors.Add("ManifestVersion is required.");
            else if (manifest.ManifestVersion > SampleManifest.CurrentVersion) 
                result.Errors.Add("Unsupported ManifestVersion: " + manifest.ManifestVersion + ".");
            if (string.IsNullOrWhiteSpace(manifest.RootDirectory)) 
                result.Errors.Add("RootDirectory is required.");
            if (manifest.ProcessedWidth <= 0 || manifest.ProcessedHeight <= 0) 
                result.Errors.Add("Processed dimensions must be positive.");
            if (manifest.SourceWidth <= 0 || manifest.SourceHeight <= 0) 
                result.Errors.Add("Source dimensions must be positive.");
            if (manifest.ManifestVersion >= 2)
            {
                if (manifest.ProcessedChannelCount < 0) 
                    result.Errors.Add("ProcessedChannelCount must not be negative.");
                if (manifest.ProcessedBitDepth < 0) 
                    result.Errors.Add("ProcessedBitDepth must not be negative.");
                if (manifest.SourceToProcessedTransform != null && !IsValidTransform(manifest.SourceToProcessedTransform)) 
                    result.Errors.Add("SourceToProcessedTransform must be a finite 3x3 affine matrix.");
                if (requireFiles && 
                    !string.IsNullOrWhiteSpace(manifest.ProcessedSamplePath) && 
                    !File.Exists(manifest.ProcessedSamplePath)) 
                    result.Errors.Add("ProcessedSamplePath unreadable/missing: " + manifest.ProcessedSamplePath);
            }
            if (manifest.Tiles == null || manifest.Tiles.Count == 0) 
            { 
                result.Errors.Add("Tiles must not be empty."); 
                return result; 
            }
            var orders = new HashSet<int>(); 
            var cells = new HashSet<string>();
            foreach (var t in manifest.Tiles)
            {
                if (t == null) 
                { 
                    result.Errors.Add("Tile is null."); 
                    continue; 
                }
                if (!orders.Add(t.OrderIndex)) 
                    result.Errors.Add("Duplicate OrderIndex: " + t.OrderIndex + ".");
                var cell = t.Row + "," + t.Column;
                if (!cells.Add(cell)) 
                    result.Errors.Add("Duplicate Row/Column: " + cell + ".");
                if (t.OrderIndex < 0) 
                    result.Errors.Add("Negative OrderIndex: " + t.OrderIndex + ".");
                if (t.Row < 0 || t.Column < 0) 
                    result.Errors.Add("Negative Row/Column at order " + t.OrderIndex + ".");
                if (t.ExpectedX < 0 || t.ExpectedY < 0) 
                    result.Errors.Add("Negative ExpectedX/Y at order " + t.OrderIndex + ".");
                if (t.Width <= 0 || t.Height <= 0) 
                    result.Errors.Add("Non-positive Width/Height at order " + t.OrderIndex + ".");
                if (t.ExpectedX + t.Width > manifest.ProcessedWidth || t.ExpectedY + t.Height > manifest.ProcessedHeight) 
                    result.Errors.Add("Tile outside processed image at order " + t.OrderIndex + ".");
                if (string.IsNullOrWhiteSpace(t.ExpectedPath)) 
                    result.Errors.Add("ExpectedPath missing at order " + t.OrderIndex + ".");
                else if (requireFiles && !File.Exists(t.ExpectedPath)) 
                    result.Errors.Add("ExpectedPath unreadable/missing at order " + t.OrderIndex + ": " + t.ExpectedPath);
                var exactZero = string.Equals(t.SampleContentClass, "ExactZero", StringComparison.Ordinal);
                var requiresModel = manifest.ManifestVersion < 5 || string.Equals(t.SampleContentClass, "Matchable", StringComparison.Ordinal);
                if (manifest.ManifestVersion >= 3 && requiresModel)
                {
                    if (string.IsNullOrWhiteSpace(t.NccModelPath))
                        result.Errors.Add("NccModelPath missing at order " + t.OrderIndex + ".");
                    else if (requireFiles && !File.Exists(t.NccModelPath))
                        result.Errors.Add("NccModelPath unreadable/missing at order " + t.OrderIndex + ": " + t.NccModelPath);
                }
                if (manifest.ManifestVersion >= 4 && requiresModel)
                {
                    if (t.NccModelSchemaVersion <= 0) result.Errors.Add("NccModelSchemaVersion missing at order " + t.OrderIndex + ".");
                    if (!t.NccModelRoiX.HasValue || !t.NccModelRoiY.HasValue || !t.NccModelRoiWidth.HasValue || !t.NccModelRoiHeight.HasValue)
                        result.Errors.Add("NCC model ROI metadata missing at order " + t.OrderIndex + ".");
                    else if (t.NccModelRoiX < 0 || t.NccModelRoiY < 0 || t.NccModelRoiWidth <= 0 || t.NccModelRoiHeight <= 0 || t.NccModelRoiX + t.NccModelRoiWidth > t.Width || t.NccModelRoiY + t.NccModelRoiHeight > t.Height)
                        result.Errors.Add("NCC model ROI is outside tile at order " + t.OrderIndex + ".");
                    if (!t.NccReferenceOriginRow.HasValue || !t.NccReferenceOriginColumn.HasValue)
                        result.Errors.Add("NCC reference origin metadata missing at order " + t.OrderIndex + ".");
                }
                if (manifest.ManifestVersion >= 5)
                {
                    GerberViewer.Stitching.Imaging.SampleTileContentClass parsed;
                    if (!Enum.TryParse(t.SampleContentClass, out parsed)) result.Errors.Add("SampleContentClass missing/invalid at order " + t.OrderIndex + ".");
                    if (t.SampleNonZeroPixelRatio < 0 || t.SampleNonZeroPixelRatio > 1) result.Errors.Add("SampleNonZeroPixelRatio outside [0,1] at order " + t.OrderIndex + ".");
                    if (exactZero && (t.SampleMinGray != 0 || t.SampleMaxGray != 0 || t.SampleNonZeroPixelRatio != 0)) result.Errors.Add("ExactZero metrics inconsistent at order " + t.OrderIndex + ".");
                    if (!string.IsNullOrWhiteSpace(t.ShapeModelPath))
                    {
                        if (requireFiles && !File.Exists(t.ShapeModelPath)) result.Errors.Add("ShapeModelPath unreadable/missing at order " + t.OrderIndex + ": " + t.ShapeModelPath);
                        if (t.ShapeModelSchemaVersion <= 0 || !t.ShapeModelRoiX.HasValue || !t.ShapeModelRoiY.HasValue || !t.ShapeModelRoiWidth.HasValue || !t.ShapeModelRoiHeight.HasValue || !t.ShapeReferenceOriginRow.HasValue || !t.ShapeReferenceOriginColumn.HasValue) result.Errors.Add("Shape model metadata missing at order " + t.OrderIndex + ".");
                        else if (t.ShapeModelRoiX < 0 || t.ShapeModelRoiY < 0 || t.ShapeModelRoiWidth <= 0 || t.ShapeModelRoiHeight <= 0 || t.ShapeModelRoiX + t.ShapeModelRoiWidth > t.Width || t.ShapeModelRoiY + t.ShapeModelRoiHeight > t.Height) result.Errors.Add("Shape model ROI is outside tile at order " + t.OrderIndex + ".");
                    }
                }
            }
            for (int i = 0; i < manifest.Tiles.Count; i++) 
                if (!orders.Contains(i)) 
                    result.Errors.Add("Missing OrderIndex: " + i + ".");
            return result;
        }

        private static bool IsValidTransform(double[][] h)
        {
            if (h == null || h.Length != 3) return false;
            for (int r = 0; r < 3; r++)
            {
                if (h[r] == null || h[r].Length != 3) 
                    return false;
                for (int c = 0; c < 3; c++) 
                    if (double.IsNaN(h[r][c]) || double.IsInfinity(h[r][c])) 
                        return false;
            }
            return Math.Abs(h[2][0]) < 1e-12 && Math.Abs(h[2][1]) < 1e-12 && Math.Abs(h[2][2] - 1.0) < 1e-12;
        }
    }
}
