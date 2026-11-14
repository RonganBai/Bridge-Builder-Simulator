using System;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    public static partial class SpecialVehicleFactory
    {
        private static void Beacon(Node3D n,float x,float y,float w,string[] colors)
        {
            Box(n,new(x,y-0.08f,0),new(0.35f,0.09f,w*0.8f),Dark);
            for(int i=0;i<colors.Length;i++)
            {
                string name="EmergencyBeacon"+colors[i];
                var color=new Color(EmergencyBeaconPattern.ColorFor(name));
                var mat=new StandardMaterial3D{ResourceName=name,AlbedoColor=color.Darkened(0.55f),Roughness=0.25f};
                float z=w*0.72f*((i+0.5f)/colors.Length-0.5f);
                Box(n,new(x,y,z),new(0.3f,0.16f,w*0.68f/colors.Length),mat);
                n.AddChild(new OmniLight3D{Name=name+"Light"+n.GetChildCount(),Position=new(x,y+0.12f,z),LightColor=color,LightEnergy=2.5f,OmniRange=4.5f,ShadowEnabled=false,Visible=false});
            }
        }
        private static void BuildAmericanCab(Node3D n,float front,float w,Material paint)
        {
            Box(n,new(-1.6f,0.84f,0),new(7,0.28f,w*0.65f),Dark);
            Round(n,new(front-0.8f,1.65f,0),new(1.6f,1.05f,w*0.72f),0.12f,paint);
            var cabOutline = new[] { new Vector2(front-3.5f,.8f), new Vector2(front-1.6f,.8f),
                new Vector2(front-1.6f,2.35f), new Vector2(front-1.82f,3.30f),
                new Vector2(front-2.04f,3.60f), new Vector2(front-3.5f,3.60f) };
            n.AddChild(new MeshInstance3D { Mesh=CurvedMeshes.Extrude(cabOutline,w*.92f),MaterialOverride=paint });
            Round(n,new(front-4.15f,2.35f,0),new(1.3f,3.1f,w*0.94f),0.14f,paint);
            CurvedMeshes.Windshield(n,front-1.612f,2.46f,front-1.795f,3.25f,w*.74f,0f,Glass);
            Box(n,new(front+0.03f,1.58f,0),new(0.08f,0.93f,w*0.64f),Alloy);
            for(int i=-4;i<=4;i++)Box(n,new(front+0.08f,1.58f,i*w*0.065f),new(0.025f,0.81f,0.04f),Dark);
            Box(n,new(front+0.08f,0.94f,0),new(0.18f,0.3f,w),Alloy);
            foreach(int side in new[]{-1,1})
            {
                Box(n,new(front-2.64f,2.85f,side*w*0.465f),new(1.02f,0.65f,0.018f),Glass);
                Box(n,new(front-2.55f,2.28f,side*w*0.47f),new(0.3f,0.06f,0.03f),Alloy);
                Box(n,new(front-2.45f,1.05f,side*w*0.46f),new(1.6f,0.14f,0.28f),Alloy);
                Bar(n,new(front-3.5f,1.1f,side*w*0.5f),new(front-3.5f,4.05f,side*w*0.5f),0.09f,Alloy);
                Box(n,new(front+0.09f,1.3f,side*w*0.4f),new(0.08f,0.23f,0.35f),Lamp);
                Box(n,new(front-1.7f,2.75f,side*w*0.54f),new(0.14f,0.4f,0.07f),Alloy);
            }
        }
        private static bool BuildAdditional(VehicleDefinition d,Func<float,float,Node3D> unit,Material paint)
        {
            if(d.Kind is not ("mixer" or "fire_engine" or "flatbed" or "police" or "police_suv" or "jeep" or "utility" or "crane"))return false;
            float l=(float)d.Length,w=(float)d.Width,h=(float)d.Height,rear=(float)d.Axles.Min(a=>a.Offset),front=(l+rear)/2,back=front-l,cx=rear/2;
            Node3D n;
            if(d.Kind=="police_suv")
            {
                Node3D suv=null;
                SuvVehicleFactory.Build(d,(f,r)=>suv=unit(f,r),paint);n=suv;
            }
            else
            {
                n=unit(0,rear);
                Box(n,new(cx,0.55f,0),new(l*0.94f,0.23f,w*0.7f),Dark);
                if(d.Kind is "police" or "jeep")
                {
                    bool jeep=d.Kind=="jeep";
                    Round(n,new(cx,0.83f,0),new(l,0.6f,w),jeep?0.07f:0.2f,paint);
                    float cf=front-l*(jeep?0.29f:0.32f),cr=back+l*(jeep?0.12f:0.24f),roof=h-0.2f;
                    var profile=new[]{new Vector2(cr,1.05f),new Vector2(cf,1.05f),new Vector2(cf-(jeep?0.15f:0.4f),roof),new Vector2(cr+(jeep?0.03f:0.25f),roof)};
                    n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(profile,w*0.88f),MaterialOverride=jeep?Dark:paint});
                    CurvedMeshes.Windshield(n,cf+0.01f,1.12f,cf-(jeep?0.15f:0.4f),roof-0.06f,w*0.8f,0.035f,Glass);
                    foreach(int side in new[]{-1,1})
                    {
                        Box(n,new((cf+cr)/2,1.3f,side*w*0.447f),new((cf-cr)*0.77f,Math.Max(0.22f,roof-1.2f),0.025f),Glass);
                        Box(n,new(cx,1.3f,side*w*0.45f),new(0.1f,0.65f,0.035f),Dark);
                        if(jeep){Box(n,new(cx,0.54f,side*w*0.52f),new(2.2f,0.15f,0.18f),Dark);foreach(var a in d.Axles)Box(n,new((float)a.Offset,0.95f,side*w*0.48f),new(1.1f,0.14f,0.22f),Dark);}
                    }
                    if(jeep)
                    {
                        for(int i=-3;i<=3;i++)Box(n,new(front+0.015f,0.94f,i*0.13f),new(0.04f,0.33f,0.065f),Dark);
                        n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.39f,BottomRadius=0.39f,Height=0.22f,RadialSegments=12},RotationDegrees=new(0,0,90),Position=new(back-0.12f,1.05f,0),MaterialOverride=Dark});
                        Box(n,new(back-0.24f,1.05f,0),new(0.025f,0.3f,0.3f),Alloy);
                    }
                }
                else
                {
                    float cab=front-1.05f;
                    var cabShape = new[] { new Vector2(front-2.1f,.55f),new Vector2(front,.55f),new Vector2(front,1.72f),new Vector2(front-.22f,2.50f),new Vector2(front-.42f,2.75f),new Vector2(front-2.1f,2.75f) };
                    n.AddChild(new MeshInstance3D { Mesh=CurvedMeshes.Extrude(cabShape,w*.94f),MaterialOverride=paint });
                    CurvedMeshes.Windshield(n,front+.012f,1.78f,front-.20f,2.49f,w*.80f,.025f,Glass);
                    Box(n,new(front+0.04f,1.17f,0),new(0.06f,0.55f,w*0.6f),Dark);
                    foreach(int side in new[]{-1,1})
                    {
                        Box(n,new(cab+0.15f,2.18f,side*w*0.471f),new(1.08f,0.62f,0.018f),Glass);
                        Box(n,new(cab+0.75f,2.02f,side*w*0.54f),new(0.14f,0.38f,0.08f),Dark);
                        Box(n,new(cab-0.3f,1.56f,side*w*0.478f),new(0.32f,0.06f,0.025f),Alloy);
                    }
                    float deckFront=front-2.25f,deckL=deckFront-back,deckX=(deckFront+back)/2;
                    Box(n,new(deckX,1.02f,0),new(deckL,0.25f,w),Alloy);
                    if(d.Kind=="mixer")
                    {
                        BuildMixerDrum(n,deckX,back);
                    }
                    if(d.Kind=="fire_engine")
                    {
                        Box(n,new(deckX,1.92f,0),new(deckL,1.65f,w*0.96f),paint);
                        foreach(int side in new[]{-1,1})
                        {
                            for(int i=0;i<4;i++){float x=back+0.6f+i*(deckL-1.2f)/3;Box(n,new(x,1.94f,side*w*0.486f),new((deckL-0.8f)/4,1.12f,0.035f),Alloy);Box(n,new(x,1.5f,side*w*0.51f),new(0.3f,0.05f,0.035f),Dark);}
                            Box(n,new(cx,1.18f,side*w*0.505f),new(l*0.9f,0.16f,0.025f),White);

                        }
                        BuildAerialLadder(n,back,deckFront,w);
                        Beacon(n,cab,2.88f,w,new[]{"Red","White","Red"});
                    }
                    if(d.Kind=="utility")
                    {
                        BuildUtilityBody(n,deckX,deckL,back,front,w,paint);
                    }
                    if(d.Kind=="crane")BuildCraneBody(n,deckX,deckL,back,front,w,paint);
                    if(d.Kind=="flatbed")
                    {
                        var excavator=Payload(n,"PayloadExcavator");
                        var yellow=new StandardMaterial3D{AlbedoColor=new Color("D7AB3C"),Roughness=0.65f};
                        float mx=deckX-0.2f;
                        foreach(int side in new[]{-1,1}){Round(excavator,new(mx,1.48f,side*0.82f),new(4.3f,0.66f,0.65f),0.22f,Dark);var rollers=new Node3D{Position=new(0,1.18f,0)};excavator.AddChild(rollers);for(int i=0;i<6;i++)Wheel(rollers,mx-1.6f+i*0.64f,0.24f,side*1.12f,0.06f);}
                        Round(excavator,new(mx,2.04f,0),new(3.5f,0.75f,2.1f),0.1f,yellow);
                        Box(excavator,new(mx-0.5f,2.98f,-0.48f),new(1.6f,1.25f,1.05f),yellow);
                        Box(excavator,new(mx+0.315f,3.05f,-0.48f),new(0.035f,0.86f,0.84f),Glass);
                        Box(excavator,new(mx-0.5f,3.08f,-1.02f),new(1.24f,0.9f,0.025f),Glass);
                        Bar(excavator,new(mx+0.7f,2.3f,0.45f),new(mx+2.2f,3.65f,0.45f),0.19f,yellow);
                        Bar(excavator,new(mx+2.2f,3.65f,0.45f),new(mx+3.1f,1.9f,0.45f),0.15f,yellow);
                        Box(excavator,new(mx+3.1f,1.55f,0.45f),new(0.9f,0.6f,1.1f),Dark);
                        foreach(float x in new[]{mx-1.5f,mx+1.5f})foreach(int side in new[]{-1,1})Bar(excavator,new(x,1.15f,side*1.2f),new(x+0.45f,2.36f,side*0.8f),0.025f,Alloy);
                        BuildPayloadOptions(n,deckX,deckFront,back,w);
                        foreach(int side in new[]{-1,1}){var ramp=new MeshInstance3D{Mesh=new BoxMesh{Size=new(1.3f,0.12f,0.6f)},Position=new(back+0.3f,1.45f,side*0.8f),RotationDegrees=new(0,0,-55),MaterialOverride=Alloy};n.AddChild(ramp);}
                    }
                }
                foreach(var a in d.Axles)foreach(int side in new[]{-1,1})Wheel(n,(float)a.Offset,d.Kind is "police"?0.36f:0.46f,side*(w/2-0.12f),0.28f);
                foreach(int side in new[]{-1,1}){Box(n,new(front+0.025f,0.92f,side*w*0.34f),new(0.06f,0.2f,0.32f),Lamp);Box(n,new(back-0.02f,0.86f,side*w*0.36f),new(0.05f,0.19f,0.22f),Red);}
                Box(n,new(front+0.04f,0.6f,0),new(0.12f,0.18f,w*0.94f),Dark);
            }
            if(d.Kind is "police" or "police_suv")
            {
                foreach(int side in new[]{-1,1})
                {
                    Box(n,new(cx,0.96f,side*w*0.505f),new(l*0.45f,0.35f,0.018f),White);
                    VehicleText(n,"POLICE",new(cx,0.97f,side*w*0.516f),new(0,side>0?0:180,0),0.0035f,48,new Color("202b34"));
                }
                Beacon(n,cx,h-0.02f,w,new[]{"Blue","White","Red"});
                Box(n,new(front+0.13f,0.75f,0),new(0.13f,0.5f,w*0.55f),Dark);
            }
            return true;
        }
    }
}
