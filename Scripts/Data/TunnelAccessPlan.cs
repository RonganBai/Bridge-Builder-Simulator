using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    /// <summary>
    /// One source for the bank lifts: shaft positions, the ground cut-outs, camera bounds and the traffic route.
    /// The tube ends sit against the bank walls; a vertical lift behind each wall carries vehicles between the
    /// bank road and the tube floor (no ramps, so the whole crossing fits a normal camera view).
    /// </summary>
    public sealed class TunnelAccessPlan
    {
        public TunnelGeometry Geometry { get; }
        public List<(double x,double y)> Route { get; }
        /// <summary>Guide lines through each lift (top centre → bottom centre → tube end, and back up on the right).</summary>
        public List<(double x,double y)> LeftRamp { get; }
        public List<(double x,double y)> RightRamp { get; }
        public TunnelRules.LiftShaft Left { get; }
        public TunnelRules.LiftShaft Right { get; }
        /// <summary>Ground cut-outs for the shafts: horizontal strips (x0, y0, x1, y1) stacked from the bottom up.</summary>
        public List<(double x0,double y0,double x1,double y1)> Cuts { get; } = new();
        /// <summary>Lift tops on the bank road.</summary>
        public Vec3d Entry { get; }
        public Vec3d Exit { get; }
        public bool LeftConnected { get; }
        public bool RightConnected { get; }
        public double MinX => Left.X0 - 4;
        public double MaxX => Right.X1 + 4;

        public TunnelAccessPlan(LevelDefinition level,TunnelGeometry geometry)
        {
            Geometry=geometry;Route=TunnelRules.Route(level,geometry);
            var s=TunnelRules.Shafts(level,geometry);Left=s[0];Right=s[1];
            TunnelRules.FlatBed(level.Terrain,out double b0,out double b1);
            LeftConnected=geometry.Points[0].X<=b0+TunnelRules.JoinTolerance;
            RightConnected=geometry.Points[^1].X>=b1-TunnelRules.JoinTolerance;
            Entry=new Vec3d(Left.CenterX,Left.Top);Exit=new Vec3d(Right.CenterX,Right.Top);
            LeftRamp=new(){(Left.CenterX,Left.Top),(Left.CenterX,Left.Bottom),(Left.X1,Left.Bottom)};
            RightRamp=new(){(Right.X0,Right.Bottom),(Right.CenterX,Right.Bottom),(Right.CenterX,Right.Top)};
            double step=Math.Max(1,geometry.Type.TubeHeight-geometry.Type.FloorHeight-0.5);
            foreach(var sh in s)
                for(double y=sh.Bottom;y<sh.Top+step;y+=step)
                    Cuts.Add((sh.X0-0.4,Math.Min(y,sh.Top+0.2),sh.X1+0.4,Math.Min(y,sh.Top+0.2)));
        }

        /// <summary>Default wall-to-wall tube of this type (before anything is built).</summary>
        public static TunnelGeometry Preview(LevelDefinition level,TunnelTypeConfig type,double first=double.NaN,double second=double.NaN)
        {
            TunnelRules.FlatBed(level.Terrain,out double left,out double right);
            double bottom=TunnelRules.DefaultBottom(level.Terrain,type);
            var geometry=new TunnelGeometry{Type=type,LiftLength=TunnelRules.LiftCfg.MaxLength};
            geometry.Points.Add(new Vec3d(left,bottom));geometry.Points.Add(new Vec3d(right,bottom));return geometry;
        }
    }
}
