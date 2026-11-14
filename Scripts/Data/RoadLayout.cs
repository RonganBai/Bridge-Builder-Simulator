using System;

namespace BridgeArchitect3D.Data
{
    /// <summary>Shared transverse presentation: one lane per direction, right-hand traffic.</summary>
    public static class RoadLayout
    {
        public const float Width=8f,LaneWidth=4f,LaneCenter=2f,TrussOffset=4.3f;
        // The engineering solver remains a 2D section model. This is the transverse drawing envelope,
        // separate from the hydraulic displacement section used by existing tunnel balance parameters.
        public static float TubeEnvelope(double sectionWidth)=>Math.Max((float)sectionWidth,Width+6);
        public static float Z(int direction)=>direction>=0?LaneCenter:-LaneCenter;
        public static bool FitsLane(double width)=>width<=LaneWidth-.2;
        public static double HarborX(double progress,int direction)=>direction>=0?progress:24-progress;
    }
}
