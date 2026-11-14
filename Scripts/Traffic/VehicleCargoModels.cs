using System;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    public static partial class SpecialVehicleFactory
    {
        // One continuous surface of revolution; all sections share the same tilted axis.
        private static Mesh Revolved(Vector2[] profile,int segments=24)
        {
            var st=new SurfaceTool();st.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 P(Vector2 p,int i){float a=i*Mathf.Tau/segments;return new(p.X,p.Y*Mathf.Cos(a),p.Y*Mathf.Sin(a));}
            void Tri(Vector3 a,Vector3 b,Vector3 c)
            {
                var normal=(b-a).Cross(c-a).Normalized();
                st.SetNormal(normal);st.AddVertex(a);st.SetNormal(normal);st.AddVertex(c);st.SetNormal(normal);st.AddVertex(b);
            }
            for(int j=0;j<profile.Length-1;j++)for(int i=0;i<segments;i++)
            {
                var a=P(profile[j],i);var b=P(profile[j],i+1);var c=P(profile[j+1],i+1);var d=P(profile[j+1],i);
                Tri(a,b,c);Tri(a,c,d);
            }
            return st.Commit();
        }
        private static void BuildMixerDrum(Node3D n,float deckX,float back)
        {
            var assembly=new Node3D{Name="MixerDrumAxis",Position=new(deckX,2.22f,0),RotationDegrees=new(0,0,-12)};n.AddChild(assembly);
            var profile=new[]{new Vector2(-2.15f,0),new Vector2(-2.15f,0.32f),new Vector2(-1.9f,0.42f),new Vector2(-1.15f,0.94f),new Vector2(-0.7f,1.04f),new Vector2(0.65f,1.04f),new Vector2(1.1f,0.91f),new Vector2(1.65f,0.52f),new Vector2(1.85f,0.22f),new Vector2(1.85f,0)};
            assembly.AddChild(new MeshInstance3D{Name="ContinuousDrum",Mesh=Revolved(profile.Select(p=>new Vector2(p.X*1.3f,p.Y*1.13f)).ToArray()),MaterialOverride=White});
            foreach(float x in new[]{-0.64f,0.5f})
                assembly.AddChild(new MeshInstance3D{Mesh=Revolved(new[]{new Vector2((x-0.065f)*1.3f,1.179f),new Vector2((x+0.065f)*1.3f,1.179f)}),MaterialOverride=Red});
            foreach(float x in new[]{deckX-0.9f,deckX+1.65f})
            {
                Box(n,new(x,1.36f,0),new(0.34f,0.5f,1.55f),Dark);
                foreach(int side in new[]{-1,1})Bar(n,new(x,1.4f,side*0.55f),new(x,1.78f,side*0.75f),0.12f,Alloy);
            }
            Box(n,new(back+0.25f,2.92f,0),new(0.65f,0.16f,0.78f),Alloy);
            Box(n,new(back+0.32f,3.11f,0),new(0.5f,0.32f,0.6f),White);
            foreach(int side in new[]{-1,1})Bar(n,new(back+0.25f,1.18f,side*0.43f),new(back+0.25f,2.85f,side*0.43f),0.05f,Alloy);
            var chute=new Node3D{Position=new(back+0.25f,2.1f,0),RotationDegrees=new(0,0,28)};n.AddChild(chute);
            Box(chute,new(-0.5f,0,0),new(1.3f,0.09f,0.42f),Alloy);
            foreach(int side in new[]{-1,1})Box(chute,new(-0.5f,0.1f,side*0.23f),new(1.3f,0.24f,0.05f),Alloy);
        }

        private static void BuildCoupe(Node3D n,Material paint,float length=4.55f,float width=1.88f,float rear=-2.7f)
        {
            float front=(length+rear)/2,cx=rear/2,back=front-length;
            Round(n,new(cx,0.64f,0),new(length,0.5f,width),0.17f,paint);
            var profile=new[]{new Vector2(back+0.65f,0.86f),new Vector2(front-1.1f,0.86f),new Vector2(front-1.83f,1.28f),new Vector2(back+1.22f,1.3f)};
            n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(profile,width*0.84f),MaterialOverride=paint});
            CurvedMeshes.Windshield(n,front-1.08f,0.91f,front-1.82f,1.25f,width*0.77f,0.03f,Glass);
            CurvedMeshes.Windshield(n,back+0.64f,0.91f,back+1.23f,1.26f,width*0.77f,-0.03f,Glass);
            foreach(int side in new[]{-1,1})
            {
                SideWindow(n,new[]{new Vector2(back+0.94f,0.95f),new Vector2(front-1.3f,0.95f),new Vector2(front-1.87f,1.22f),new Vector2(back+1.3f,1.24f)},side*width*0.425f,Glass);
                // A single long door and handle on each side distinguishes the two-door coupe.
                Box(n,new(cx+0.05f,0.79f,side*width*0.504f),new(1.65f,0.025f,0.02f),Dark);
                Box(n,new(cx-0.65f,0.76f,side*width*0.505f),new(0.025f,0.36f,0.02f),Dark);
                Box(n,new(cx-0.45f,0.86f,side*width*0.51f),new(0.23f,0.035f,0.025f),Alloy);
                foreach(float x in new[]{0f,rear})Wheel(n,x,0.32f,side*(width/2-0.1f),0.24f);
                Box(n,new(front+0.01f,0.72f,side*width*0.34f),new(0.04f,0.09f,0.43f),Lamp);
                Box(n,new(back-0.01f,0.73f,side*width*0.34f),new(0.04f,0.09f,0.4f),Red);
                Box(n,new(front-1.22f,0.96f,side*width*0.52f),new(0.2f,0.1f,0.13f),paint);
            }
            Box(n,new(front+0.02f,0.49f,0),new(0.04f,0.17f,width*0.65f),Dark);
        }
        private static Node3D Payload(Node3D n,string name)
        {
            var payload=new Node3D{Name=name,Visible=name=="PayloadExcavator"};payload.SetMeta("vehicle_variant",true);n.AddChild(payload);return payload;
        }
        private static void BuildPayloadOptions(Node3D n,float deckX,float deckFront,float back,float w)
        {
            var yellow=new StandardMaterial3D{AlbedoColor=new Color("dfa92f"),Roughness=0.6f};
            var forklifts=Payload(n,"PayloadForklifts");
            for(int i=0;i<3;i++)
            {
                var f=new Node3D{Position=new(back+1.7f+i*2.7f,1.15f,0)};forklifts.AddChild(f);
                Round(f,new(0,0.59f,0),new(1.75f,0.62f,1.2f),0.12f,yellow);
                Box(f,new(0.06f,0.9f,0),new(0.5f,0.25f,0.52f),Dark);
                Box(f,new(-0.19f,1.1f,0),new(0.12f,0.65f,0.5f),Dark);
                foreach(int side in new[]{-1,1})
                {
                    foreach(float x in new[]{-0.58f,0.58f})Wheel(f,x,0.29f,side*0.57f,0.18f);
                    foreach(float x in new[]{-0.65f,0.55f})Bar(f,new(x,0.75f,side*0.53f),new(x,1.92f,side*0.53f),0.04f,Dark);
                    Box(f,new(0.94f,1.12f,side*0.36f),new(0.1f,2.15f,0.1f),Dark);
                    Box(f,new(1.33f,0.18f,side*0.34f),new(0.85f,0.08f,0.13f),Alloy);
                    Bar(f,new(-0.6f,0.25f,side*0.76f),new(-0.1f,0.9f,side*0.5f),0.024f,Alloy);
                }
                Box(f,new(-0.02f,1.98f,0),new(1.5f,0.1f,1.24f),yellow);
                Box(f,new(0.95f,0.55f,0),new(0.12f,0.15f,0.94f),Alloy);
            }
            var coils=Payload(n,"PayloadSteel");
            for(int i=0;i<3;i++)
            {
                float x=back+1.65f+i*2.85f;
                foreach(int side in new[]{-1,1})Box(coils,new(x,1.26f,side*0.68f),new(1.6f,0.22f,0.28f),yellow);
                var coil=new MeshInstance3D{Position=new(x,2.08f,0),RotationDegrees=new(0,90,0),Mesh=Revolved(new[]{new Vector2(-0.82f,0.34f),new Vector2(-0.82f,0.91f),new Vector2(0.82f,0.91f),new Vector2(0.82f,0.34f),new Vector2(-0.82f,0.34f)}),MaterialOverride=Alloy};coils.AddChild(coil);
                for(int k=0;k<3;k++)coils.AddChild(new MeshInstance3D{Position=new(x,2.08f,-0.64f+k*0.64f),RotationDegrees=new(0,90,0),Mesh=Revolved(new[]{new Vector2(-0.035f,0.915f),new Vector2(0.035f,0.915f)}),MaterialOverride=Dark});
                foreach(int side in new[]{-1,1})Bar(coils,new(x-0.65f,1.16f,side*w*0.45f),new(x,2.85f,side*0.45f),0.025f,Dark);
            }
            var transformer=Payload(n,"PayloadTransformer");
            Box(transformer,new(deckX,1.3f,0),new(4.6f,0.28f,2.2f),Dark);
            var gray=new StandardMaterial3D{AlbedoColor=new Color("70867e"),Roughness=0.65f};
            Round(transformer,new(deckX,2.15f,0),new(3.9f,1.5f,1.62f),0.08f,gray);
            foreach(int side in new[]{-1,1})for(int i=0;i<16;i++)Box(transformer,new(deckX-1.7f+i*0.225f,2.16f,side*0.98f),new(0.07f,1.3f,0.44f),gray);
            for(int i=0;i<3;i++)
            {
                float x=deckX-1.2f+i*1.2f;
                Bar(transformer,new(x,2.9f,0),new(x,3.62f,0),0.1f,White);
                for(int j=0;j<5;j++)transformer.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.18f,BottomRadius=0.18f,Height=0.05f,RadialSegments=10},Position=new(x,3.05f+j*0.11f,0),MaterialOverride=White});
                Box(transformer,new(x,3.68f,0),new(0.21f,0.1f,0.2f),Alloy);
            }
            foreach(float x in new[]{deckX-1.5f,deckX+1.5f})foreach(int side in new[]{-1,1})Bar(transformer,new(x,1.16f,side*1.23f),new(x,2.92f,side*0.78f),0.03f,Alloy);
        }
        private static void BuildCarCarrier(VehicleDefinition d,Func<float,float,Node3D> unit,Material paint)
        {
            float w=(float)d.Width,rear=(float)d.Axles.Min(a=>a.Offset),front=((float)d.Length+rear)/2;
            var cab=unit(0,-4.9f);BuildAmericanCab(cab,front,w,paint);
            const float pivot=-4.8f;
            var trailer=unit(pivot,rear);float end=front-(float)d.Length-pivot,start=0.9f,length=start-end,center=(start+end)/2;
            foreach(float y in new[]{1.05f,2.98f})
            {
                foreach(int side in new[]{-1,1})Box(trailer,new(center,y,side*w*0.32f),new(length,0.12f,0.56f),Alloy);
                foreach(float x in new[]{start,end})Box(trailer,new(x,y,0),new(0.12f,0.14f,w*0.92f),paint);
            }
            foreach(int side in new[]{-1,1})
            {
                Box(trailer,new(center,0.81f,side*w*0.36f),new(length,0.22f,0.12f),Dark);
                for(int i=0;i<5;i++)
                {
                    float x=start-0.25f-i*(length-0.5f)/4;
                    Box(trailer,new(x,2.03f,side*w*0.47f),new(0.1f,2.15f,0.1f),paint);
                    if(i<4)Bar(trailer,new(x,1.14f,side*w*0.47f),new(x-(length-0.5f)/4,2.92f,side*w*0.47f),0.045f,paint);
                }
                Box(trailer,new(center,3.14f,side*w*0.47f),new(length,0.08f,0.08f),paint);
                var ramp=new MeshInstance3D{Mesh=new BoxMesh{Size=new(1.3f,0.12f,0.55f)},Position=new(end+0.25f,1.42f,side*w*0.32f),RotationDegrees=new(0,0,-42),MaterialOverride=Alloy};trailer.AddChild(ramp);
                Box(trailer,new(end-0.04f,0.86f,side*w*0.36f),new(0.06f,0.2f,0.25f),Red);
            }
            string[] colors={"b84f43","517f9c","d6d3bf","586c56","bba54a","65738b","a4aca9","8d5f68"};
            for(int level=0;level<2;level++)for(int i=0;i<4;i++)
            {
                float nose=start-0.25f-i*4.45f;
                var car=new Node3D{Position=new(nose-0.77f,level==0?1.12f:3.05f,0)};trailer.AddChild(car);
                BuildCoupe(car,new StandardMaterial3D{AlbedoColor=new Color(colors[level*4+i]),Roughness=0.45f},4.1f,1.8f,-2.55f);
                foreach(float x in new[]{nose-0.75f,nose-3.3f})foreach(int side in new[]{-1,1})Box(trailer,new(x,level==0?1.18f:3.11f,side*0.78f),new(0.35f,0.1f,0.22f),Dark);
            }
            foreach(var axle in d.Axles)foreach(int side in new[]{-1,1})
                Wheel(axle.Offset>-6?cab:trailer,(float)(axle.Offset>-6?axle.Offset:axle.Offset-pivot),0.46f,side*(w/2-0.17f),0.34f);
        }
    }
}
