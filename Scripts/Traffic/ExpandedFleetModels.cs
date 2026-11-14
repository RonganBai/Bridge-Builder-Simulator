using System;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    public static partial class SpecialVehicleFactory
    {
        private static void FleetCab(Node3D n,float front,float w,Material paint,float height=2.8f)
        {
            Round(n,new(front-1.05f,height/2+0.35f,0),new(2.1f,height-0.7f,w*0.94f),0.12f,paint);
            Box(n,new(front+0.015f,height-0.65f,0),new(0.03f,0.75f,w*0.8f),Glass);
            Box(n,new(front+0.03f,1.13f,0),new(0.05f,0.44f,w*0.6f),Dark);
            foreach(int side in new[]{-1,1})
            {
                Box(n,new(front-0.85f,height-0.65f,side*w*0.475f),new(1.5f,0.72f,0.03f),Glass);
                Box(n,new(front-0.15f,2.12f,side*w*0.54f),new(0.15f,0.32f,0.08f),Dark);
                Box(n,new(front+0.04f,0.88f,side*w*0.34f),new(0.05f,0.18f,0.35f),Lamp);
            }
            Box(n,new(front+0.04f,0.63f,0),new(0.14f,0.2f,w),Dark);
        }
        private static void FleetWheels(VehicleDefinition d,Node3D n,float origin=0,float radius=0.46f)
        {
            foreach(var a in d.Axles)foreach(int side in new[]{-1,1})Wheel(n,(float)a.Offset-origin,radius,side*((float)d.Width/2-0.14f),0.3f);
        }
        private static void BusBody(Node3D n,float front,float back,float w,float h,Material paint,bool school=false)
        {
            float center=(front+back)/2,length=front-back;
            Round(n,new(center,h/2+0.24f,0),new(length,h-0.5f,w),0.12f,paint);
            for(float x=back+0.65f;x<front-0.5f;x+=1.12f)foreach(int side in new[]{-1,1})
                Box(n,new(x,h-0.85f,side*w*0.505f),new(0.9f,0.83f,0.025f),Glass);
            Box(n,new(front+0.02f,h-0.9f,0),new(0.04f,1.02f,w*0.84f),Glass);
            foreach(int side in new[]{-1,1})
            {
                Box(n,new(front+0.04f,0.86f,side*w*0.35f),new(0.06f,0.2f,0.32f),Lamp);
                Box(n,new(back-0.02f,0.91f,side*w*0.36f),new(0.06f,0.22f,0.22f),Red);
                if(school)foreach(float y in new[]{0.95f,1.15f,1.36f})Box(n,new(center,y,side*w*0.506f),new(length*0.97f,0.055f,0.025f),Dark);
            }
            AddPassengerDoor(n,front-1,w/2+.028f,h);
            Box(n,new(front+0.04f,h-0.2f,0),new(0.05f,0.28f,w*0.6f),Dark);
        }
        private static bool BuildExpandedFleet(VehicleDefinition d,Func<float,float,Node3D> unit,Material paint)
        {
            if(d.Kind is not ("dump" or "articulated_bus" or "tanker" or "tractor" or "garbage" or "pump" or "modular" or "school_bus"))return false;
            float rear=(float)d.Axles.Min(a=>a.Offset),front=((float)d.Length+rear)/2,back=front-(float)d.Length,w=(float)d.Width,h=(float)d.Height;
            if(d.Kind=="articulated_bus")
            {
                var lead=unit(0,-5.6f);BusBody(lead,front,-6.5f,w,h,paint);
                var tail=unit(-7.8f,rear);BusBody(tail,0,back+7.8f,w,h,paint);
                // Closely spaced flexible bellows visually bridge the two independently pitched bodies.
                for(int i=0;i<12;i++)Box(lead,new(-6.55f-i*0.105f,1.77f,0),new(0.055f,2.48f,w*0.95f),Dark);
                foreach(var a in d.Axles)foreach(int side in new[]{-1,1})Wheel(a.Offset>=-5.6?lead:tail,(float)(a.Offset>=-5.6?a.Offset:a.Offset+7.8),0.43f,side*(w/2-0.14f),0.28f);
                return true;
            }
            if(d.Kind=="tanker")
            {
                var cab=unit(0,-4.5f);BuildAmericanCab(cab,front,w,paint);
                const float pivot=-4.8f;var tank=unit(pivot,rear);float end=back-pivot,start=0.6f,length=start-end,cx=(start+end)/2;
                Box(tank,new(cx,1.03f,0),new(length,0.22f,w*0.72f),Dark);
                tank.AddChild(new MeshInstance3D{Mesh=Revolved(new[]{new Vector2(end,0),new Vector2(end+0.25f,0.75f),new Vector2(end+0.6f,1.16f),new Vector2(start-0.6f,1.16f),new Vector2(start-0.25f,0.75f),new Vector2(start,0)}),Position=new(0,2.42f,0),MaterialOverride=Alloy});
                foreach(int side in new[]{-1,1})
                {
                    Box(tank,new(cx,2.35f,side*1.17f),new(length*0.88f,0.22f,0.04f),paint);
                    Box(tank,new(cx,3.7f,side*0.4f),new(length*0.8f,0.06f,0.055f),Dark);
                    for(int i=0;i<6;i++)Box(tank,new(end+0.7f,1.2f+i*0.36f,side*0.7f),new(0.065f,0.055f,0.22f),Alloy);
                }
                for(int i=0;i<3;i++)tank.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.22f,BottomRadius=0.22f,Height=0.13f,RadialSegments=12},Position=new(end+2+i*(length-4)/2,3.61f,0),MaterialOverride=Dark});
                foreach(var a in d.Axles)foreach(int side in new[]{-1,1})Wheel(a.Offset>-6?cab:tank,(float)(a.Offset>-6?a.Offset:a.Offset-pivot),0.46f,side*(w/2-0.15f),0.33f);
                return true;
            }
            if(d.Kind=="tractor")
            {
                var tractor=unit(0,-2.5f);float tf=front;
                FarmTractor(tractor,tf,paint);
                const float pivot=-3.5f;var trailer=unit(pivot,rear);float end=back-pivot,cx=(end-0.35f)/2,len=-0.35f-end;
                Bar(trailer,new(0.35f,0.6f,0),new(-0.8f,0.65f,0),0.1f,Dark);
                Box(trailer,new(cx,0.92f,0),new(len,0.2f,2.25f),Alloy);
                foreach(int side in new[]{-1,1})Box(trailer,new(cx,1.37f,side*1.08f),new(len,0.8f,0.12f),paint);
                foreach(float x in new[]{end+0.05f,-0.4f})Box(trailer,new(x,1.37f,0),new(0.12f,0.8f,2.2f),paint);
                var hay=new StandardMaterial3D{AlbedoColor=new Color("bd9f55"),Roughness=1};
                for(int i=0;i<3;i++)Box(trailer,new(end+0.7f+i*1.25f,1.92f,0),new(1.1f,1.45f,1.85f),hay);
                foreach(var a in d.Axles.Where(a=>a.Offset<-3))foreach(int side in new[]{-1,1})Wheel(trailer,(float)a.Offset-pivot,0.45f,side*1.02f,0.28f);
                return true;
            }
            var n=unit(0,rear);float center=rear/2;
            Box(n,new(center,0.61f,0),new((float)d.Length*0.95f,0.26f,w*0.7f),Dark);
            if(d.Kind=="school_bus")
            {
                SchoolBus(n,d,front,back,w,paint);
            }
            else if(d.Kind=="modular")
            {
                Box(n,new(center,1.03f,0),new((float)d.Length,0.36f,w),paint);
                for(int i=0;i<12;i++)foreach(int side in new[]{-1,1})Box(n,new(front-0.5f-i*((float)d.Length-1)/11,1.04f,side*w*0.505f),new(0.35f,0.23f,0.035f),Dark);
                var load=new StandardMaterial3D{AlbedoColor=new Color("8c9898"),Roughness=0.6f};
                Round(n,new(center,2.55f,0),new((float)d.Length*0.68f,2.55f,w*0.79f),0.13f,load);
                for(int i=0;i<8;i++)foreach(int side in new[]{-1,1})Box(n,new(center-(float)d.Length*0.3f+i*(float)d.Length*0.6f/7,2.5f,side*w*0.4f),new(0.1f,2.35f,0.09f),Alloy);
                foreach(float x in new[]{center-(float)d.Length*0.26f,center+(float)d.Length*0.26f})foreach(int side in new[]{-1,1})Bar(n,new(x,1.24f,side*w*0.48f),new(x,3.85f,side*w*0.3f),0.04f,Dark);
                Box(n,new(front-0.55f,1.54f,0),new(0.75f,0.66f,1.1f),Dark);
                AmberMarker(n,new(front-0.5f,1.95f,0),new(0.28f,0.14f,0.28f));
            }
            else
            {
                FleetCab(n,front,w,paint);float start=front-2.25f,len=start-back,cx=(start+back)/2;
                if(d.Kind=="dump")
                {
                    Box(n,new(cx,1.13f,0),new(len,0.25f,w*0.95f),Alloy);
                    foreach(int side in new[]{-1,1})
                    {
                        Box(n,new(cx,1.92f,side*w*0.46f),new(len,1.45f,0.14f),paint);
                        for(int i=0;i<6;i++)Box(n,new(back+0.3f+i*(len-0.6f)/5,1.91f,side*w*0.5f),new(0.09f,1.5f,0.07f),Dark);
                    }
                    foreach(float x in new[]{back+0.1f,start-0.1f})Box(n,new(x,1.92f,0),new(0.15f,1.45f,w*0.95f),paint);
                    if(d.Id!="dump_empty")
                    {
                        var rock=new StandardMaterial3D{AlbedoColor=new Color("827c70"),Roughness=1};
                        Box(n,new(cx,1.8f,0),new(len-0.3f,0.95f,w*0.82f),rock);
                        for(int i=0;i<5;i++)for(int j=0;j<3;j++)n.AddChild(new MeshInstance3D{Mesh=new SphereMesh{Radius=0.46f,Height=0.64f,RadialSegments=6,Rings=3},Position=new(back+0.65f+i*(len-1.3f)/4,2.43f,j*0.62f-0.62f),MaterialOverride=rock});
                    }
                }
                if(d.Kind=="garbage")
                {
                    Round(n,new(cx+0.25f,1.98f,0),new(len-0.5f,2.18f,w*0.96f),0.24f,paint);
                    Round(n,new(back+0.4f,1.92f,0),new(0.85f,1.9f,w),0.13f,Dark);
                    Box(n,new(back+0.15f,1.25f,0),new(0.24f,0.68f,w*0.77f),Alloy);
                    foreach(int side in new[]{-1,1}){Box(n,new(cx,1.96f,side*w*0.487f),new(len*0.7f,0.42f,0.03f),White);Bar(n,new(back+0.8f,1.16f,side*w*0.42f),new(back+0.3f,2.8f,side*w*0.42f),0.09f,Alloy);AmberMarker(n,new(back+0.15f,2.98f,side*w*0.36f),new(0.26f,0.14f,0.2f));}
                }
                if(d.Kind=="pump")
                {
                    Box(n,new(cx,1.18f,0),new(len,0.55f,w*0.98f),paint);
                    n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.65f,BottomRadius=0.65f,Height=0.45f,RadialSegments=12},Position=new(start-1.2f,1.68f,0),MaterialOverride=Dark});
                    for(int i=0;i<4;i++)
                    {
                        float y=2.08f+i*0.34f,z=-0.55f+i*0.34f;
                        Box(n,new(cx,y,z),new(len-0.65f-i*0.3f,0.25f,0.23f),i%2==0?Red:White);
                        Bar(n,new(back+0.55f,y,z+0.18f),new(start-0.7f,y,z+0.18f),0.065f,Dark);
                        Box(n,new(i%2==0?back+0.6f:start-0.7f,y+0.14f,z),new(0.3f,0.5f,0.35f),Alloy);
                    }
                    foreach(int side in new[]{-1,1})foreach(float x in new[]{back+1, start-0.5f}){Box(n,new(x,0.96f,side*w*0.4f),new(0.7f,0.38f,0.56f),Dark);Bar(n,new(x,0.75f,side*w*0.45f),new(x,1.48f,side*w*0.45f),0.12f,Alloy);}
                    Box(n,new(back+0.28f,1.87f,0),new(0.58f,0.7f,1.15f),Dark);
                    Beacon(n,front-1,2.9f,w,new[]{"Amber","Amber"});
                }
            }
            FleetWheels(d,n,0,d.Kind=="modular"?0.34f:0.46f);
            foreach(int side in new[]{-1,1})Box(n,new(back-0.01f,0.79f,side*w*0.35f),new(0.035f,0.18f,0.24f),Red);
            return true;
        }
    }
}
