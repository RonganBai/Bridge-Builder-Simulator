using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

/// <summary>Tunnel access by bank lifts (they replaced the automatic ramps and portals).</summary>
internal static class TunnelAccessTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        foreach(var fixture in new[]{("level13","tunnel_immersed"),("level14","tunnel_floating"),("level15","tunnel_shield")})
        {
            var level=db.GetLevel(fixture.Item1);var editor=new DesignEditor(null,level,db);
            TunnelRules.FlatBed(level.Terrain,out double left,out double right);
            string why=editor.PreviewTunnel(left,right,fixture.Item2,out double preview,out _);
            // One click anywhere builds the whole tube wall to wall
            var built=editor.AddTunnel(left+7,left+9,fixture.Item2);var geometry=editor.TunnelGeo;
            check(fixture.Item2+" one click builds the tube wall to wall",built.Ok&&geometry.Points[0].X==left&&geometry.Points[^1].X==right,built.Message);
            check(fixture.Item2+" preview matches the committed total",why==null&&Math.Abs(preview-editor.UsedCost)<1e-6,$"{preview} / {editor.UsedCost}");
            if(!geometry.Type.Straight)
            {
                // Bendable tubes: every draggable point is an element joint and only moves vertically
                bool onJoints=geometry.Points.Count==geometry.Count+1&&Enumerable.Range(0,geometry.Count).All(i=>geometry.X0[i]==geometry.Points[i].X&&geometry.X1[i]==geometry.Points[i+1].X);
                check(fixture.Item2+" draggable points sit on the element joints",onJoints&&editor.Design.Tunnel.PointNodeIds.Count==geometry.Count+1,$"{geometry.Points.Count} pts / {geometry.Count} elements");
                var joint=editor.Design.Tunnel.PointNodeIds.Select(editor.Design.FindNode).OrderBy(n=>n.Position.X).ElementAt(2);
                double jx=joint.Position.X,jy=joint.Position.Y;
                var moved=editor.MoveNode(joint.Id,jx+3,jy-1);
                check(fixture.Item2+" a joint point moves only vertically and the elements follow",moved.Ok&&Math.Abs(joint.Position.X-jx)<1e-9&&Math.Abs(editor.TunnelGeo.Y1[1]-(jy-1))<1e-9&&Math.Abs(editor.TunnelGeo.Y0[2]-(jy-1))<1e-9,moved.Message);
                check(fixture.Item2+" joint points cannot be deleted or added",!editor.DeleteNode(joint.Id).Ok&&!editor.InsertTunnelPoint(jx+4).Ok,"");
                editor.Undo();geometry=editor.TunnelGeo;
            }
            var plan=new TunnelAccessPlan(level,geometry);
            check(fixture.Item2+" lifts stand right behind both walls",Math.Abs(plan.Left.X1-left)<1e-9&&Math.Abs(plan.Right.X0-right)<1e-9&&plan.Left.Top>plan.Left.Bottom&&plan.Right.Top>plan.Right.Bottom,"");
            check(fixture.Item2+" lifts reach from the bank road to the tube floor",Math.Abs(plan.Left.Bottom-(geometry.Points[0].Y+geometry.Type.FloorHeight))<1e-9&&Math.Abs(plan.Left.Top-TunnelRules.EntranceY(level,true,0))<1e-9,"");
            check(fixture.Item2+" camera bounds hold both lifts and the tube (no long ramps)",plan.MinX<plan.Left.X0&&plan.MaxX>plan.Right.X1&&plan.MaxX-plan.MinX<level.Terrain.RiverWidth+2*TunnelRules.LiftCfg.MaxLength+10,"");
            double price=TunnelRules.Cost(geometry,level),old=geometry.Type.RampCostPerMeter;
            try
            {
                geometry.Type.RampCostPerMeter=1e9;
                check(fixture.Item2+" old ramp prices no longer charge anything",Math.Abs(TunnelRules.Cost(geometry,level)-price)<1e-8,"");
            }
            finally {geometry.Type.RampCostPerMeter=old;}
            geometry.PortalRaise+=1;geometry.FloodGate=true;geometry.Pump=true;
            check(fixture.Item2+" old portal protection fields cost nothing",Math.Abs(TunnelRules.Cost(geometry,level)-price)<1e-8,"");
            var reference=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns",fixture.Item1+"_reference.json")));
            var simulation=new BridgeSimulation(reference,level,db);simulation.Initialize();
            var dispatch=typeof(BridgeSimulation).GetMethod("LaneFor",BindingFlags.Instance|BindingFlags.NonPublic);
            int Lane(int lane,string vehicle)=>(int)dispatch.Invoke(simulation,new object[]{lane,db.Vehicles[vehicle]});
            check(fixture.Item2+" intact reference opens a drivable tunnel with two lifts",simulation.TunnelOpen&&simulation.Lifts.Count==2&&simulation.Lifts[0].Down&&!simulation.Lifts[1].Down,simulation.Result.FailureMessage??"");
            check(fixture.Item2+" road cars prefer the available tunnel",Lane(0,"car")==TunnelRules.Lane,"");
            check(fixture.Item2+" trains remain on the bridge",Lane(0,"train")==0,"");
            check(fixture.Item2+" upper road routing remains on its original lane",Lane(1,"car")==1,"");
        }
    }
}
