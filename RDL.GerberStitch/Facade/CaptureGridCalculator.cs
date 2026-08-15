using System;
using System.Collections.Generic;

namespace RDL.GerberStitch.Facade
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Dựng lưới chụp theo ĐÚNG công thức RDL_Master.
    // Nguồn: MapEngine/MapCore.cs:1154 (x = col * captureLayout.PitchX) và :1167
    // (FOV_Image.X = (int)(FOV_World.X / camResX)). Overlap ở MapCore.cs:1073 KHÔNG tham gia phép tính vị trí
    // (khối dùng nó đã bị comment ở 1082-1088) -- nó chỉ là đại lượng dẫn xuất, xem MainForm.cs:3243.
    // Thuần số học: không I/O, không HALCON, không OpenCV.]
    public static class CaptureGridCalculator
    {
        public static CaptureGridResult Build(CaptureGridSpec spec)
        {
            if (spec == null)
                throw new ArgumentNullException("spec");
            if (spec.CamResX <= 0d || spec.CamResY <= 0d)
                throw new ArgumentException("CamResX/CamResY phải > 0 (µm/px).", "spec");
            if (spec.CapturePitchX <= 0d || spec.CapturePitchY <= 0d)
                throw new ArgumentException("CapturePitchX/CapturePitchY phải > 0 (µm).", "spec");
            if (spec.ImageWidth <= 0 || spec.ImageHeight <= 0)
                throw new ArgumentException("ImageWidth/ImageHeight phải > 0.", "spec");
            if (spec.Rows <= 0 || spec.Columns <= 0)
                throw new ArgumentException("Rows/Columns phải > 0.", "spec");

            double stepX = spec.CapturePitchX / spec.CamResX;
            double stepY = spec.CapturePitchY / spec.CamResY;

            int tileWidth = spec.TileWidth.HasValue && spec.TileWidth.Value > 0
                                ? spec.TileWidth.Value
                                : spec.ImageWidth;
            int tileHeight = spec.TileHeight.HasValue && spec.TileHeight.Value > 0
                                 ? spec.TileHeight.Value
                                 : spec.ImageHeight;
            if (tileWidth < spec.ImageWidth || tileHeight < spec.ImageHeight)
                throw new ArgumentException(
                    "TileWidth/TileHeight phải >= ImageWidth/ImageHeight; cửa sổ crop không được nhỏ hơn ảnh chụp.",
                    "spec");

            // Master thay -1 bằng nửa ảnh (MainForm.cs:3233-3237). Mặc định 0 khớp StartX_EdgePath=0.
            double startOffsetX = spec.StartOffsetX == -1d ? spec.ImageWidth / 2d : spec.StartOffsetX;
            double startOffsetY = spec.StartOffsetY == -1d ? spec.ImageHeight / 2d : spec.StartOffsetY;

            // Biên tìm kiếm dôi ra chia ĐỀU hai phía. Hiện trạng cũ dồn hết về phải/dưới.
            int marginX = (tileWidth - spec.ImageWidth) / 2;
            int marginY = (tileHeight - spec.ImageHeight) / 2;

            var result = new CaptureGridResult
            {
                StepXPx = stepX,
                StepYPx = stepY,
                CaptureOverlapXPx = spec.ImageWidth - stepX,
                CaptureOverlapYPx = spec.ImageHeight - stepY,
                TileOverlapXPx = tileWidth - stepX,
                TileOverlapYPx = tileHeight - stepY,
                TileWidth = tileWidth,
                TileHeight = tileHeight,
                RequiredWidth = (int)((spec.Columns - 1) * stepX) + tileWidth,
                RequiredHeight = (int)((spec.Rows - 1) * stepY) + tileHeight
            };

            if (result.CaptureOverlapXPx <= 0d || result.CaptureOverlapYPx <= 0d)
                result.Warnings.Add(
                    "CaptureOverlap <= 0: các ảnh chụp kề nhau KHÔNG chồng nhau (X=" +
                    result.CaptureOverlapXPx.ToString("0.##") + ", Y=" +
                    result.CaptureOverlapYPx.ToString("0.##") +
                    "). Neighbor recovery sẽ không có vùng chung để đo.");

            foreach (var rc in EnumerateOrder(spec))
            {
                int row = rc.Key;
                int column = rc.Value;

                // ExpectedX = (int)((c*PitchX - StartOffsetX*CamResX) / CamResX) = (int)(c*stepX - StartOffsetX)
                int expectedX = (int)(column * stepX - startOffsetX);
                int expectedY = (int)(row * stepY - startOffsetY);

                int left = expectedX - marginX;
                int top = expectedY - marginY;
                int right = left + tileWidth;
                int bottom = top + tileHeight;

                if (spec.RasterWidth > 0)
                {
                    left = Math.Max(0, Math.Min(spec.RasterWidth - 1, left));
                    right = Math.Max(left + 1, Math.Min(spec.RasterWidth, right));
                }
                else if (left < 0)
                {
                    left = 0;
                    right = left + tileWidth;
                }

                if (spec.RasterHeight > 0)
                {
                    top = Math.Max(0, Math.Min(spec.RasterHeight - 1, top));
                    bottom = Math.Max(top + 1, Math.Min(spec.RasterHeight, bottom));
                }
                else if (top < 0)
                {
                    top = 0;
                    bottom = top + tileHeight;
                }

                int orderIndex = result.Tiles.Count;
                if (right - left != tileWidth || bottom - top != tileHeight)
                    result.ClampedTileIndices.Add(orderIndex);

                result.Tiles.Add(new TileRect
                {
                    OrderIndex = orderIndex,
                    Row = row,
                    Column = column,
                    X = left,
                    Y = top,
                    Width = right - left,
                    Height = bottom - top
                });
            }

            if (spec.RasterWidth > 0 && result.RequiredWidth > spec.RasterWidth)
                result.Warnings.Add("Lưới tràn bề rộng raster " +
                                    (result.RequiredWidth - spec.RasterWidth) + " px (cần " +
                                    result.RequiredWidth + ", raster " + spec.RasterWidth + ").");
            if (spec.RasterHeight > 0 && result.RequiredHeight > spec.RasterHeight)
                result.Warnings.Add("Lưới tràn chiều cao raster " +
                                    (result.RequiredHeight - spec.RasterHeight) + " px (cần " +
                                    result.RequiredHeight + ", raster " + spec.RasterHeight + ").");
            if (result.ClampedTileIndices.Count > 0)
                result.Warnings.Add(result.ClampedTileIndices.Count +
                                    " tile bị kẹp vào biên raster nên nhỏ hơn kích thước tile khai báo.");

            return result;
        }

        /// <summary>Sinh (row, column) theo đúng thứ tự OrderIndex của Master.
        /// ColumnMajorZigzag: idx(r,c) = c*Rows + (c chẵn ? r : Rows-1-r).</summary>
        private static IEnumerable<KeyValuePair<int, int>> EnumerateOrder(CaptureGridSpec spec)
        {
            if (spec.Order == CaptureOrder.RowMajorZigzag)
            {
                for (int r = 0; r < spec.Rows; r++)
                    for (int i = 0; i < spec.Columns; i++)
                    {
                        int c = r % 2 == 0 ? i : spec.Columns - 1 - i;
                        yield return new KeyValuePair<int, int>(r, c);
                    }
                yield break;
            }

            for (int c = 0; c < spec.Columns; c++)
                for (int i = 0; i < spec.Rows; i++)
                {
                    int r = c % 2 == 0 ? i : spec.Rows - 1 - i;
                    yield return new KeyValuePair<int, int>(r, c);
                }
        }
    }
}
