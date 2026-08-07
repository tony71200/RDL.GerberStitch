using System;
using System.Collections.Generic;
using System.Linq;

namespace GerberViewer.Stitching.RobotManager
{
    public enum OrderMode 
    { 
        Zigzag = 0, 
        Branch = 1, 
        BranchDown = 2 
    }
    public enum StartCorner 
    { 
        TopLeft = 0, 
        TopRight = 1, 
        BottomLeft = 2, 
        BottomRight = 3 
    }
    public enum RobotMovement 
    { 
        Left = 0, 
        Right = 1, 
        Up = 2, 
        Down = 3 
    }
    public enum StartOrder 
    { 
        TopLeftRight = 0, 
        TopLeftDown = 1, 
        BottomRightLeft = 2, 
        BottomRightUp = 3 
    }

    public sealed class StartOrderResolution
    {
        public StartCorner StartCorner { get; set; }
        public RobotMovement RobotMovement { get; set; }
    }

    public static class StartOrderResolver
    {
        public static StartOrderResolution Resolve(StartOrder order)
        {
            switch (order)
            {
                case StartOrder.TopLeftRight: 
                    return new StartOrderResolution 
                    { 
                        StartCorner = StartCorner.TopLeft, 
                        RobotMovement = RobotMovement.Right 
                    };
                case StartOrder.TopLeftDown: 
                    return new StartOrderResolution 
                    { 
                        StartCorner = StartCorner.TopLeft, 
                        RobotMovement = RobotMovement.Down 
                    };
                case StartOrder.BottomRightLeft: 
                    return new StartOrderResolution 
                    { 
                        StartCorner = StartCorner.BottomRight,
                        RobotMovement = RobotMovement.Left 
                    };
                case StartOrder.BottomRightUp: 
                    return new StartOrderResolution 
                    { 
                        StartCorner = StartCorner.BottomRight, 
                        RobotMovement = RobotMovement.Up 
                    };
                default: throw new ArgumentOutOfRangeException(nameof(order), order, "Unsupported StartOrder version 1 preset.");
            }
        }
    }

    public sealed class ImageInfo
    {
        public ImageInfo(
            string filePath, 
            int groupId, 
            int imageId, 
            int? positionId, 
            double xRobot, 
            double yRobot, 
            int row = -1, 
            int column = -1)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            GroupId = groupId; 
            ImageId = imageId; 
            PositionId = positionId; 
            XRobot = xRobot; 
            YRobot = yRobot; 
            Row = row; 
            Column = column;
        }
        public string FilePath { get; }
        public int GroupId { get; }
        public int ImageId { get; }
        public int? PositionId { get; }
        public double XRobot { get; }
        public double YRobot { get; }
        public int Row { get; }
        public int Column { get; }
        public override string ToString() => $"G{GroupId} #{ImageId} [{Row},{Column}] P{(PositionId.HasValue ? PositionId.Value.ToString() : "None")} (x={XRobot:0.###}, y={YRobot:0.###})";
    }

    public sealed class OrderOptions
    {
        public OrderMode Mode { get; set; } = OrderMode.Zigzag;
        public StartCorner StartCorner { get; set; } = StartCorner.TopLeft;
        public RobotMovement RobotMovement { get; set; } = RobotMovement.Down;
        public int NodeInterval { get; set; } = 21;
        public static OrderOptions FromStartOrder(StartOrder order, OrderMode mode = OrderMode.Zigzag)
        {
            var resolved = StartOrderResolver.Resolve(order);
            return new OrderOptions 
            { 
                Mode = mode, 
                StartCorner = resolved.StartCorner, 
                RobotMovement = resolved.RobotMovement 
            };
        }
    }

    public sealed class Bounds2D
    {
        public double MinX { get; set; } public double MinY { get; set; } public double MaxX { get; set; } public double MaxY { get; set; }
        public static Bounds2D FromPoints(IEnumerable<ImageInfo> points)
        {
            var items = (points ?? Enumerable.Empty<ImageInfo>()).ToArray();
            if (items.Length == 0) 
                return new Bounds2D();
            return new Bounds2D 
            { 
                MinX = items.Min(p => p.XRobot), 
                MinY = items.Min(p => p.YRobot), 
                MaxX = items.Max(p => p.XRobot), 
                MaxY = items.Max(p => p.YRobot) 
            };
        }
    }

    public sealed class NodeLinks 
    { 
        public int ImageId { get; set; } 
        public int? HNext { get; set; } 
        public int? VNext { get; set; } 
        public int? Prev { get; set; } 
        public int? Next { get; set; }
    }
    public sealed class GridCell
    {
        public GridCell(int row, int column)
        {
            Row = row; Column = column;
        }
        public int Row { get; }
        public int Column { get; }
    }

    public enum EdgeDir { Horizontal, Vertical }
    public sealed class PathSegment 
    { 
        public int FromId { get; set; } 
        public int ToId { get; set; } 
        public GridCell FromCell { get; set; } 
        public GridCell ToCell { get; set; } 
        public EdgeDir Direction { get; set; } 
    }
}
