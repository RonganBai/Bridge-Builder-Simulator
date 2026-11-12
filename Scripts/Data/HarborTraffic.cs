using System;
using System.Collections.Generic;
using System.Linq;

namespace BridgeArchitect3D.Data
{
    public enum HarborOperation { RoadOpen, Draining, LoweringGates, Opening, ShipPassage, Closing, RaisingGates }
    public sealed class HarborCar
    {
        public long Id { get; internal set; }
        public VehicleDefinition Definition { get; internal set; }
        public double Front { get; internal set; }
        public double Speed { get; internal set; }
        public double CruiseSpeed { get; internal set; }
        public bool EmergencyLightsOn { get; internal set; }
        public bool Committed { get; internal set; }
        internal double ReactionUntil;
        public double Rear=>Front-Definition.Length;
        public double AxleX=>Front-(Definition.Length-Definition.Wheelbase)/2;
    }
    public sealed class HarborBoat
    {
        public TrafficEvent Definition { get; internal set; }
        public bool RequiresOpening { get; internal set; }
        public double Z { get; internal set; }
        public double Rear=>Z-Definition.Length/2;
    }
    /// <summary>Persistent FIFO traffic and a clearance-driven bridge controller for the attract scene.</summary>
    public sealed class HarborTraffic
    {
        public const double StopLine=-5.7,FixedStep=0.02,OpeningSeconds=8;
        public const double NavigationSafetyMargin=0.2;
        private readonly List<HarborCar> _cars=new();
        private readonly List<HarborBoat> _boats=new();
        private readonly VehicleDefinition[] _fleet;
        private readonly double _referenceMass,_referenceSpeed;
        private readonly double _waterLevel,_closedNavigationBottom;
        private double _accumulator,_stateTime,_nextSpawn;
        private int _nextVehicle,_nextShip;
        private long _nextId;
        private HarborBoat _crossing;
        public double Time { get; private set; }
        public HarborOperation Operation { get; private set; }=HarborOperation.RoadOpen;
        public IReadOnlyList<VehicleDefinition> Fleet=>_fleet;
        public IReadOnlyList<HarborCar> Cars=>_cars;
        public IReadOnlyList<HarborBoat> Boats=>_boats;
        public bool RoadGreen=>Operation==HarborOperation.RoadOpen;
        public double Opening=>Operation switch
        {
            HarborOperation.Opening=>HarborTimeline.Smooth(_stateTime/OpeningSeconds),
            HarborOperation.ShipPassage=>1,
            HarborOperation.Closing=>1-HarborTimeline.Smooth(_stateTime/OpeningSeconds),
            _=>0
        };
        public double GateRaised=>Operation switch
        {
            HarborOperation.RoadOpen or HarborOperation.Draining=>1,
            HarborOperation.LoweringGates=>1-HarborTimeline.Smooth(_stateTime/2),
            HarborOperation.RaisingGates=>HarborTimeline.Smooth(_stateTime/2),
            _=>0
        };
        public HarborTraffic(IEnumerable<VehicleDefinition> definitions,double waterLevel=HarborTimeline.WaterLevel,double closedNavigationBottom=HarborTimeline.ClosedNavigationBottom)
        {
            if(!double.IsFinite(waterLevel) || !double.IsFinite(closedNavigationBottom))throw new ArgumentOutOfRangeException(nameof(waterLevel));
            _waterLevel=waterLevel;_closedNavigationBottom=closedNavigationBottom;
            _fleet=definitions.Where(v=>v.Kind!="tank" && v.Kind!="train" && v.Kind!="pedestrians")
                .OrderBy(v=>v.Id=="car"?0:1).ThenBy(v=>v.Id).ToArray();
            if(_fleet.Length==0)throw new ArgumentException("Harbor needs at least one road vehicle.");
            var sedan=_fleet.FirstOrDefault(v=>v.Id=="car");
            _referenceMass=Math.Max(250,sedan?.Mass??1500);_referenceSpeed=Math.Max(1,sedan?.Speed??8);
        }
        public double CruiseSpeedFor(VehicleDefinition definition)=>definition.Kind=="ambulance"?_referenceSpeed*1.2:
            Math.Clamp(_referenceSpeed*Math.Pow(_referenceMass/Math.Max(250,definition.Mass),0.2),_referenceSpeed*0.42,_referenceSpeed*1.25);
        public bool CanPassClosed(TrafficEvent definition)
        {
            double top=ShipGeometry.Solids(definition).SelectMany(s=>s.Vertices).Select(v=>v.Y)
                .Concat(ShipGeometry.Parts(definition).Select(p=>p.Y+p.Height/2)).Max();
            return _waterLevel+top+NavigationSafetyMargin<=_closedNavigationBottom;
        }
        public void Advance(double seconds)
        {
            if(!double.IsFinite(seconds) || seconds<0)throw new ArgumentOutOfRangeException(nameof(seconds));
            _accumulator+=seconds;
            while(_accumulator+1e-10>=FixedStep){Step();_accumulator-=FixedStep;}
        }
        private void Go(HarborOperation state){Operation=state;_stateTime=0;}
        private void Spawn()
        {
            if(Time<_nextSpawn)return;var definition=_fleet[_nextVehicle%_fleet.Length];
            if(_cars.Any(c=>c.Definition.Id==definition.Id))return;
            var last=_cars.OrderBy(c=>c.Front).FirstOrDefault();
            if(last!=null && last.Rear<HarborTimeline.SpawnX+Math.Max(3,definition.FollowGap))return;
            _cars.Add(new HarborCar {Id=_nextId++,Definition=definition,CruiseSpeed=CruiseSpeedFor(definition),Front=HarborTimeline.SpawnX});
            _nextVehicle++;_nextSpawn=Time+3.8;
        }
        private void BeginDrain()
        {
            foreach(var car in _cars)
            {
                double distance=StopLine-car.Front;
                car.Committed=distance<0 || car.Speed>0.3 && distance<=car.Speed*car.Speed/(2*3.8)+1.2;
            }
            Go(HarborOperation.Draining);
        }
        private void MoveCars()
        {
            HarborCar leader=null;
            foreach(var car in _cars.OrderByDescending(c=>c.Front))
            {
                double gap=Math.Max(3,car.Definition.FollowGap),limit=double.PositiveInfinity,leaderSpeed=0;
                double obstruction=double.PositiveInfinity;
                if(leader!=null){obstruction=leader.Rear;leaderSpeed=leader.Speed;limit=leader.Rear-gap;}
                if(!RoadGreen && !car.Committed && StopLine<limit)
                {limit=StopLine;obstruction=StopLine+gap;leaderSpeed=0;}
                double acceleration=1.8*(1-Math.Pow(car.Speed/Math.Max(0.1,car.CruiseSpeed),4));
                if(double.IsFinite(obstruction))
                {
                    double distance=Math.Max(0.1,obstruction-car.Front);
                    // Keep a braking reserve above the hard geometric gap. Without it,
                    // discretization can hit the spacing clamp and stop the follower abruptly.
                    double desired=gap+0.8+car.Speed*0.95+car.Speed*(car.Speed-leaderSpeed)/(2*Math.Sqrt(1.8*2.8));
                    desired=Math.Max(gap+0.8,desired);acceleration-=1.8*Math.Pow(desired/distance,2);
                }
                double speed=Math.Max(0,car.Speed+Math.Clamp(acceleration,-6,1.8)*FixedStep);
                if(!RoadGreen && !car.Committed)
                {
                    // Reserve enough distance to stop before the barrier, including this step.
                    const double braking=4.5;
                    double remaining=Math.Max(0,StopLine-car.Front),step=braking*FixedStep;
                    speed=Math.Min(speed,Math.Max(0,Math.Sqrt(step*step+2*braking*remaining)-step));
                }
                if(Time<car.ReactionUntil)speed=0;
                double front=car.Front+(car.Speed+speed)*FixedStep/2;
                if(front>limit){front=Math.Max(car.Front,limit);speed=Math.Min(speed,Math.Max(0,(front-car.Front)/FixedStep));}
                car.Front=front;car.Speed=speed;leader=car;
                // Emergency mode is latched for this trip, including subsequent traffic stops.
                if(car.Definition.Kind=="ambulance" && speed>=Math.Min(6,car.CruiseSpeed*0.65))car.EmergencyLightsOn=true;
            }
            _cars.RemoveAll(c=>c.Rear>=HarborTimeline.RightPortal+35);
        }
        private void StartBoat()
        {
            var profile=ShipCatalog.Types[_nextShip++%ShipCatalog.Types.Count];var definition=ShipCatalog.Create(profile.Id,12);
            double upstream=Math.Max(56+definition.Length/2+6,definition.Speed*(OpeningSeconds+3)+definition.Length/2+2.5);
            _crossing=new HarborBoat {Definition=definition,RequiresOpening=!CanPassClosed(definition),Z=-upstream};_boats.Add(_crossing);
        }
        private void Step()
        {
            Time+=FixedStep;_stateTime+=FixedStep;Spawn();MoveCars();
            foreach(var boat in _boats)boat.Z+=boat.Definition.Speed*FixedStep;
            _boats.RemoveAll(b=>b.Rear>56+8);
            switch(Operation)
            {
                case HarborOperation.RoadOpen:
                    if(_crossing!=null && !_crossing.RequiresOpening)
                    {
                        // Low craft pass under the closed bridge without stopping road traffic.
                        if(_crossing.Rear>5){_crossing=null;_stateTime=0;}
                    }
                    else if(_stateTime>=26)
                    {
                        var next=ShipCatalog.Create(ShipCatalog.Types[_nextShip%ShipCatalog.Types.Count].Id,12);
                        if(CanPassClosed(next))StartBoat();else BeginDrain();
                    }
                    break;
                case HarborOperation.Draining:
                    if(!_cars.Any(c=>c.Committed && c.Rear<29))Go(HarborOperation.LoweringGates);break;
                case HarborOperation.LoweringGates:
                    if(_stateTime>=2){StartBoat();Go(HarborOperation.Opening);}break;
                case HarborOperation.Opening:
                    if(_stateTime>=OpeningSeconds)Go(HarborOperation.ShipPassage);break;
                case HarborOperation.ShipPassage:
                    if(_crossing.Rear>5)Go(HarborOperation.Closing);break;
                case HarborOperation.Closing:
                    if(_stateTime>=OpeningSeconds)Go(HarborOperation.RaisingGates);break;
                case HarborOperation.RaisingGates:
                    if(_stateTime>=2)
                    {
                        int order=0;
                        foreach(var car in _cars.OrderByDescending(c=>c.Front))
                        {car.Committed=false;if(car.Speed<0.3)car.ReactionUntil=Time+0.25+order++*0.35;}
                        _crossing=null;
                        Go(HarborOperation.RoadOpen);
                    }
                    break;
            }
        }
    }
}
