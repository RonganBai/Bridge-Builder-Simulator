using System;
using System.Linq;
using BridgeArchitect3D.Data;
internal static class RoadLayoutTests
{
 public static void Run(GameDatabase db,Action<string,bool,string> check)
 {
  check("Road: every road vehicle fits its lane",db.Vehicles.Values.Where(v=>v.Kind!="train" && v.Kind!="pedestrians").All(v=>RoadLayout.FitsLane(v.Width)),"");
  check("Road: opposing lanes stay inside the deck",RoadLayout.Z(1)==2 && RoadLayout.Z(-1)==-2 && RoadLayout.Width==2*RoadLayout.LaneWidth,"");
  var traffic=new HarborTraffic(db.Vehicles.Values,bidirectional:true);bool forward=false,reverse=false,spacing=true,clear=true;
  for(int i=0;i<6000;i++)
  {
   traffic.Advance(.1);
   foreach(var lane in traffic.Cars.GroupBy(c=>c.Direction))
   {
    var cars=lane.OrderByDescending(c=>c.Front).ToArray();
    for(int j=1;j<cars.Length;j++)spacing &= cars[j-1].Rear-cars[j].Front>=Math.Max(3,cars[j].Definition.FollowGap)-.01;
   }
   forward|=traffic.Cars.Any(c=>c.Direction==1);reverse|=traffic.Cars.Any(c=>c.Direction==-1);
   if(traffic.Opening>0)clear &= !traffic.Cars.Any(c=>c.Front>0 && c.Rear<24);
  }
  check("Road: both directions spawn and retain independent safe spacing",forward && reverse && spacing,"");
  check("Road: both directions clear the bridge before opening",clear,"");
 }
}
