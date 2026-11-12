using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;

internal static class SuvTests
{
    public static void Run(GameDatabase db,string root,Action<string,bool,string> check)
    {
        var compact=db.Vehicles["suv"];var large=db.Vehicles["suv_large"];
        check("SUV: the existing suv id remains usable as the compact model",compact.Kind=="suv" && compact.Name.Contains("紧凑") && db.Levels.Any(l=>l.TrafficEvents.Any(e=>e.VehicleId=="suv")),"");
        check("SUV: large variant has a longer body, wheelbase, larger height and greater mass",large.Length>compact.Length && large.Wheelbase>compact.Wheelbase && large.Height>compact.Height && large.Mass>compact.Mass && large.Width>compact.Width,"");
        foreach(var vehicle in new[]{compact,large})
        {
            check($"SUV: {vehicle.Id} axle locations and load fractions are physically consistent",vehicle.Axles.Count==2 && vehicle.Axles[0].Offset==0 && Math.Abs(vehicle.Axles[1].Offset+vehicle.Wheelbase)<1e-9 && Math.Abs(vehicle.Axles.Sum(a=>a.LoadFraction)-1)<1e-9 && vehicle.Axles.All(a=>a.LoadFraction>0),"");
            check($"SUV: {vehicle.Id} fits the existing tunnel clearance",vehicle.Height+0.24<6.0 && vehicle.Width<3.5 && HarborTimeline.SpawnX-vehicle.Length>-97,"");
        }
        double Stress(VehicleDefinition vehicle)
        {
            var level=JsonSerializer.Deserialize<LevelDefinition>(JsonSerializer.Serialize(db.GetLevel("level3"),GameDatabase.JsonOptions),GameDatabase.JsonOptions);
            level.TrafficEvents.Clear();level.TrafficEvents.Add(new TrafficEvent {VehicleId=vehicle.Id});
            var bridge=GameDatabase.DeserializeDesign(File.ReadAllText(Path.Combine(root,"Data/ReferenceDesigns/level3_reference.json")));
            var simulation=new BridgeSimulation(bridge,level,db);var result=simulation.RunToEnd();
            check($"SUV: {vehicle.Id} completes a real bridge crossing",result.IsSuccess,result.FailureMessage);return result.MaxUtilization;
        }
        double light=Stress(compact),heavy=Stress(large);
        check("SUV: larger mass produces greater structural demand on the same reference bridge",heavy>light,$"compact={light}, large={heavy}");
    }
}
