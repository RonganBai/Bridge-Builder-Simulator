using System;
using System.Linq;
using System.Collections.Generic;
using BridgeArchitect3D.Data;

internal static class HarborTests
{
    public static void Run(GameDatabase db,Action<string,bool,string> check)
    {
        foreach(var d in db.Vehicles.Values)
        {
            check($"Catalogue: {d.Id} has category, purpose and functions",!string.IsNullOrWhiteSpace(d.Category) && !string.IsNullOrWhiteSpace(d.Purpose) && !string.IsNullOrWhiteSpace(d.Features),"");
            check($"Catalogue: {d.Id} has valid mass, dimensions, speed and axle loads",d.Mass>0 && d.Length>0 && d.Width>0 && d.Height>0 && d.Speed>0 && d.Axles.Count>0 && d.Axles.All(a=>a.LoadFraction>0) && Math.Abs(d.Axles.Sum(a=>a.LoadFraction)-1)<0.001,"");
        }
        check("Fleet: empty dump truck is lighter than loaded dump truck with identical dimensions",db.Vehicles["dump_empty"].Mass<db.Vehicles["dump_loaded"].Mass && db.Vehicles["dump_empty"].Length==db.Vehicles["dump_loaded"].Length,"");
        check("Fleet: modular transport distributes load across twelve axles",db.Vehicles["modular_transport"].Axles.Count==12 && db.Vehicles["modular_transport"].Axles.All(a=>Math.Abs(a.LoadFraction*120000-10000)<0.001),"");
        foreach(string id in new[]{"mixer","fire_engine","machinery_flatbed","police","police_suv","jeep","utility","coupe","car_carrier","crane"})
        {
            var d=db.Vehicles[id];
            check($"New fleet: {id} axle loads sum to one and align with wheelbase",Math.Abs(d.Axles.Sum(a=>a.LoadFraction)-1)<1e-9 && Math.Abs(d.Axles.Min(a=>a.Offset)+d.Wheelbase)<1e-9 && d.Axles.All(a=>a.LoadFraction>0),"");
        }
        foreach(string color in new[]{"Red","White","Blue","Amber"})
        {
            string name="EmergencyBeacon"+color;double period=color=="Amber"?2:1.2;
            bool repeats=true,hasOn=false,hasOff=false;
            for(int i=0;i<1000;i++)
            {
                double t=(i+0.371)*period/1000;bool on=EmergencyBeaconPattern.IsLit(name,t);
                repeats&=on==EmergencyBeaconPattern.IsLit(name,t+period*17);
                repeats&=on==EmergencyBeaconPattern.IsLit(name+"Light",t);
                hasOn|=on;hasOff|=!on;
            }
            check($"Beacon: {color} repeats across cycle boundaries and lenses match lights",repeats && hasOn && hasOff,"");
        }
        check("Beacon: amber remains on longer than emergency strobes",EmergencyBeaconPattern.IsLit("Amber",0.6) && !EmergencyBeaconPattern.IsLit("Red",0.6),"");
        foreach(string id in new[]{"fire_engine","police","police_suv","utility","crane"})
        {
            var emergency=new HarborTraffic(new[]{db.Vehicles[id]});bool lit=false,persists=true;
            var trips=new HashSet<long>();
            for(int i=0;i<1800;i++)
            {
                emergency.Advance(0.1);
                foreach(var car in emergency.Cars)
                {
                    if(trips.Contains(car.Id))persists&=car.EmergencyLightsOn;
                    if(car.EmergencyLightsOn){lit=true;trips.Add(car.Id);}
                }
            }
            check($"New fleet: {id} activates and latches its emergency lights",lit && persists,"");
        }
        var fleet=db.Vehicles.Values.Where(v=>v.Kind!="tank" && v.Kind!="train" && v.Kind!="pedestrians").ToArray();
        var traffic=new HarborTraffic(fleet);var seen=new HashSet<string>();var waited=new HashSet<long>();var resumed=new HashSet<long>();
        var speeds=new Dictionary<string,double>();var previousCars=new Dictionary<long,(double front,double speed)>();
        var previousBoats=new Dictionary<HarborBoat,double>();var boatKinds=new HashSet<string>();
        var litTrips=new HashSet<long>();bool sirenPersists=true,sirenWaits=false,sirenStarts=false,sirenResets=true;
        bool clear=true,gates=true,spacing=true,forward=true,boatSpeed=true,boatClear=true,wave=false,hidden=true,acceleration=true;
        bool lowBoatsGreen=true,sharedCrossing=false;int lowCrossings=0;
        int maxQueue=0,maxVisibleQueue=0;double lastTime=0,maxAcceleration=0;string accelerationDetail="";
        for(int tick=0;tick<12000;tick++)
        {
            traffic.Advance(0.1);double dt=traffic.Time-lastTime;lastTime=traffic.Time;
            var ordered=traffic.Cars.OrderByDescending(c=>c.Front).ToArray();
            maxQueue=Math.Max(maxQueue,ordered.Count(c=>c.Speed<0.15 && c.Front<HarborTraffic.StopLine+1));
            maxVisibleQueue=Math.Max(maxVisibleQueue,ordered.Count(c=>c.Speed<0.15 && c.Front>HarborTimeline.LeftPortal && c.Front<0));
            for(int i=0;i<ordered.Length;i++)
            {
                var car=ordered[i];seen.Add(car.Definition.Id);speeds[car.Definition.Id]=Math.Max(speeds.GetValueOrDefault(car.Definition.Id),car.Speed);
                if(car.Definition.Kind=="ambulance")
                {
                    if(!previousCars.ContainsKey(car.Id))sirenResets&=!car.EmergencyLightsOn;
                    if(litTrips.Contains(car.Id))sirenPersists&=car.EmergencyLightsOn;
                    if(car.EmergencyLightsOn){litTrips.Add(car.Id);sirenStarts=true;sirenWaits|=car.Speed<0.15;}
                }
                if(car.Speed<0.15 && car.Front<0)waited.Add(car.Id);
                if(waited.Contains(car.Id) && car.Front>4)resumed.Add(car.Id);
                if(traffic.Opening>1e-8 && car.Front>4 && car.Rear<20)clear=false;
                if(traffic.GateRaised<0.999 && ((car.Front>-4 && car.Rear<-4)||(car.Front>28 && car.Rear<28)))gates=false;
                if(i>0)spacing&=ordered[i-1].Rear-car.Front>=Math.Max(3,car.Definition.FollowGap)-1e-6 && ordered[i-1].Id<car.Id;
                if(previousCars.TryGetValue(car.Id,out var old))
                {
                    forward&=car.Front>=old.front-1e-8;acceleration&=Math.Abs(car.Speed-old.speed)<=6*dt+1e-4;
                    if(Math.Abs(car.Speed-old.speed)/dt>maxAcceleration){maxAcceleration=Math.Abs(car.Speed-old.speed)/dt;accelerationDetail=$"{car.Definition.Id} t={traffic.Time:0.00} {traffic.Operation} x={car.Front:0.00} v={old.speed:0.000}->{car.Speed:0.000} rate={maxAcceleration:0.00}";}
                }
                previousCars[car.Id]=(car.Front,car.Speed);
                if(i>0 && traffic.RoadGreen && waited.Contains(car.Id) && waited.Contains(ordered[i-1].Id))wave|=ordered[i-1].Speed>0.5 && car.Speed<0.15;
            }
            foreach(var boat in traffic.Boats)
            {
                boatKinds.Add(boat.Definition.ShipKind);
                if(previousBoats.TryGetValue(boat,out double z))boatSpeed&=Math.Abs(boat.Z-z-boat.Definition.Speed*dt)<1e-7;
                previousBoats[boat]=boat.Z;
                if(Math.Abs(boat.Z)<boat.Definition.Length/2+2.5)
                {
                    if(boat.RequiresOpening)boatClear&=traffic.Opening>0.999;
                    else
                    {
                        lowCrossings++;lowBoatsGreen&=traffic.RoadGreen && traffic.Opening==0 && traffic.GateRaised==1;
                        sharedCrossing|=ordered.Any(c=>c.Front>4 && c.Rear<20 && c.Speed>0.5);
                    }
                }
            }
        }
        foreach(var vehicle in fleet)
        {
            hidden&=HarborTimeline.SpawnX-vehicle.Length>-96;
            hidden&=HarborTimeline.VehicleOpacity(HarborTimeline.SpawnX,vehicle.Length)==0;
            hidden&=HarborTimeline.VehicleOpacity(HarborTimeline.RightPortal+35+vehicle.Length,vehicle.Length)==0;
        }
        check("Harbor: every road vehicle including both SUVs enters the persistent traffic",seen.Count==fleet.Length,"");
        check("Harbor: bridge waits for the complete vehicle body to leave",clear,"");
        check("Harbor: barriers never lower through a vehicle",gates,"");
        check("Harbor: queues are visible outside the tunnel and do not overlap",spacing && maxQueue>=3 && maxVisibleQueue>=2,$"queue={maxQueue}, visible={maxVisibleQueue}");
        check("Harbor: stopped drivers resume in a delayed queue discharge wave",resumed.Count>5 && wave,$"resumed={resumed.Count}, wave={wave}");
        check("Harbor: vehicles use individual cruise speeds instead of a shared speed",speeds["motorcycle"]>speeds["semi_long"]+1 && speeds.All(p=>p.Value<=traffic.CruiseSpeedFor(db.Vehicles[p.Key])+0.01),string.Join(",",speeds.Select(p=>$"{p.Key}:{p.Value:0.0}")));
        var ordinary=fleet.Where(v=>v.Kind!="ambulance").OrderBy(v=>v.Mass).ToArray();
        check("Harbor: heavier ordinary vehicles have lower weight-based speed limits",ordinary.Zip(ordinary.Skip(1)).All(p=>traffic.CruiseSpeedFor(p.First)>=traffic.CruiseSpeedFor(p.Second)),"");
        check("Harbor: ambulance cruise limit is one tier above the sedan",Math.Abs(traffic.CruiseSpeedFor(db.Vehicles["ambulance"])-traffic.CruiseSpeedFor(db.Vehicles["car"])*1.2)<1e-8,"");
        // Isolate an ambulance arriving during the red phase; the mixed fleet may clear it first.
        var ambulanceArrival=new HarborTraffic(new[]{db.Vehicles["ambulance"]});
        for(int i=0;i<1800;i++)
        {
            ambulanceArrival.Advance(0.1);
            sirenWaits|=ambulanceArrival.Cars.Any(c=>c.EmergencyLightsOn && c.Speed<0.15 && !ambulanceArrival.RoadGreen);
        }
        check("Harbor: emergency lights latch after fast driving and remain on while queued",sirenStarts && sirenPersists && sirenWaits && sirenResets,$"activated={sirenStarts}, persists={sirenPersists}, queued={sirenWaits}, resets={sirenResets}");
        check("Harbor: vehicles brake and accelerate without reversing or jumping",forward && acceleration,accelerationDetail);
        check("Harbor: four ship types move at their own constant speeds without waiting",boatKinds.Count==4 && boatSpeed && boatClear,"");
        check("Harbor: low craft cross under a closed bridge while cars keep moving",lowCrossings>0 && lowBoatsGreen && sharedCrossing,$"samples={lowCrossings}, green={lowBoatsGreen}, simultaneous={sharedCrossing}");
        var lowCraft=ShipCatalog.Create("speedboat",12);
        check("Harbor: clearance uses actual craft height and the lowest truss instead of the road surface",traffic.CanPassClosed(lowCraft) && !traffic.CanPassClosed(ShipCatalog.Create("tugboat",12)) && !traffic.CanPassClosed(ShipCatalog.Create("cargo",12)) && !traffic.CanPassClosed(ShipCatalog.Create("sailboat",12)),"");
        var highWater=new HarborTraffic(fleet,-2);var loweredTruss=new HarborTraffic(fleet,HarborTimeline.WaterLevel,-4);
        check("Harbor: rising water or a lower structure makes the same speedboat require opening",!highWater.CanPassClosed(lowCraft) && !loweredTruss.CanPassClosed(lowCraft),"");
        double craftTop=ShipGeometry.Solids(lowCraft).SelectMany(s=>s.Vertices).Select(v=>v.Y).Concat(ShipGeometry.Parts(lowCraft).Select(p=>p.Y+p.Height/2)).Max();
        double boundary=HarborTimeline.ClosedNavigationBottom-craftTop-HarborTraffic.NavigationSafetyMargin;
        check("Harbor: clearance leaves a safety gap instead of accepting a near-contact craft",new HarborTraffic(fleet,boundary-0.01).CanPassClosed(lowCraft) && !new HarborTraffic(fleet,boundary+0.01).CanPassClosed(lowCraft),"");
        check("Harbor: creation and removal remain hidden inside tunnels",hidden,"");
        var largeSteps=new HarborTraffic(fleet);var smallSteps=new HarborTraffic(fleet);
        for(int i=0;i<600;i++)largeSteps.Advance(0.1);for(int i=0;i<3000;i++)smallSteps.Advance(0.02);
        check("Harbor: frame grouping does not change the traffic or bridge state",largeSteps.Operation==smallSteps.Operation && largeSteps.Cars.Count==smallSteps.Cars.Count && largeSteps.Cars.Zip(smallSteps.Cars).All(pair=>Math.Abs(pair.First.Front-pair.Second.Front)<1e-7),"");
        check("Harbor: docks and moored boats leave a clear central navigation corridor",8+2.1*0.55/2+0.8<12-ShipCatalog.Types.Max(t=>t.Width)/2,"");
        bool boatsClear=true,bounds=true,closed=true,finite=true;
        foreach(var type in ShipCatalog.Types)
        {
            var e=ShipCatalog.Create(type.Id,12);var solids=ShipGeometry.Solids(e).ToArray();var parts=ShipGeometry.Parts(e).ToArray();
            bounds&=solids.SelectMany(s=>s.Vertices).All(v=>Math.Abs(v.X)<=e.Width/2+1e-6 && Math.Abs(v.Z)<=e.Length/2+1e-6 && v.Y<=e.Height+1e-6);
            bounds&=parts.All(p=>Math.Abs(p.X)+p.Width/2<=e.Width/2+1e-6 && Math.Abs(p.Z)+p.Length/2<=e.Length/2+1e-6 && p.Y+p.Height/2<=e.Height+1e-6);
            finite&=parts.All(p=>double.IsFinite(p.X+p.Y+p.Z+p.Width+p.Height+p.Length) && p.Width>0 && p.Height>0 && p.Length>0);
            foreach(var solid in solids)
            {
                var edges=new Dictionary<(int,int),int>();
                foreach(var face in solid.Faces)for(int i=0;i<face.Length;i++)
                {int a=face[i],b=face[(i+1)%face.Length];var key=(Math.Min(a,b),Math.Max(a,b));edges[key]=edges.GetValueOrDefault(key)+1;}
                closed&=edges.Values.All(n=>n==2);
            }
            for(double z=-e.Length/2-3;z<e.Length/2+3;z+=0.25)
            {
                double angle=HarborTimeline.OpenAngle*Math.PI/180;
                for(int side=0;side<2;side++)
                {
                    double pivot=side==0?4:20,direction=side==0?1:-1;
                    for(int i=0;i<4;i++)
                    {
                        double a=direction*i*2,b=direction*(i*2+2);
                        boatsClear&=!ShipGeometry.Intersects(e,-4.8,z,pivot+a*Math.Cos(angle),Math.Abs(a)*Math.Sin(angle)+0.1,pivot+b*Math.Cos(angle),Math.Abs(b)*Math.Sin(angle)+0.1,0,0.24,4.4);
                    }
                }
            }
            var spec=new CustomLevelSpec {Ship=true,ShipKind=type.Id,ShipHeight=type.Height};
            var level=CustomLevelBuilder.Build(spec,db);
            check($"Ship profile: {type.Id} persists through custom-level cloning and construction",spec.Clone().ShipKind==type.Id && level.TrafficEvents.Single(v=>v.Type==TrafficEventType.Ship).ShipKind==type.Id,"");
        }
        check("Ship profiles: dimensions bound every rendered solid and fitting",bounds,"");
        check("Ship profiles: all hull, sail and windshield meshes are closed solids",closed && finite,"");
        check("Harbor: all ship profiles clear the actual opening and never wait under a closing leaf",boatsClear,"");
        var sail=ShipCatalog.Create("sailboat",0);
        check("Ship collision: triangular sail hits cloth but leaves its empty upper bow corner free",ShipGeometry.Intersects(sail,0,0,-0.1,3,0.1,3,1,0.03,0.03) && !ShipGeometry.Intersects(sail,0,0,-0.1,6,0.1,6,2.4,0.03,0.03),"");
        var cargo=ShipCatalog.Create("cargo",0);var fast=ShipCatalog.Create("speedboat",0);fast.Width=cargo.Width;fast.Height=cargo.Height;fast.Length=cargo.Length;
        check("Ship collision: equal dimensions never share the wrong profile's hull cache",!ReferenceEquals(ShipSolid.Hull(cargo),ShipSolid.Hull(fast)),"");
    }
}
