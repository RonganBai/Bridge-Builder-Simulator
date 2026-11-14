using Godot;

namespace BridgeArchitect3D.Traffic
{
    public static partial class SpecialVehicleFactory
    {
        private static void AmberMarker(Node3D n,Vector3 position,Vector3 size)
        {
            var mat=new StandardMaterial3D{ResourceName="EmergencyBeaconAmber",AlbedoColor=new Color("ffb52e"),Roughness=0.25f};
            Box(n,position,size,mat);
            n.AddChild(new OmniLight3D{Name="EmergencyBeaconAmberSide"+n.GetChildCount(),Position=position,LightColor=new Color("ffb52e"),LightEnergy=1.8f,OmniRange=3.5f,ShadowEnabled=false,Visible=false});
        }
        private static void BuildUtilityBody(Node3D n,float deckX,float deckL,float back,float front,float w,Material paint)
        {
            Box(n,new(deckX,1.28f,0),new(deckL,0.35f,w*0.94f),Dark);
            foreach(int side in new[]{-1,1})
            {
                Box(n,new(deckX,1.65f,side*w*0.35f),new(deckL-0.12f,0.85f,w*0.28f),paint);
                Box(n,new(deckX,1.46f,side*w*0.498f),new(deckL-0.2f,0.13f,0.025f),Dark);
                for(int i=0;i<3;i++)
                {
                    float x=back+0.55f+i*(deckL-1.1f)/2;
                    Box(n,new(x,1.73f,side*w*0.495f),new((deckL-0.35f)/3-0.07f,0.58f,0.025f),Dark);
                    Box(n,new(x,1.74f,side*w*0.51f),new((deckL-0.35f)/3-0.15f,0.48f,0.025f),paint);
                    Box(n,new(x,1.9f,side*w*0.525f),new(0.24f,0.045f,0.025f),Dark);
                }
                AmberMarker(n,new(back+0.3f,2.12f,side*w*0.49f),new(0.35f,0.15f,0.14f));
                Box(n,new(back+0.02f,1.65f,side*w*0.34f),new(0.08f,0.75f,0.5f),paint);
            }
            // A folded two-section insulated boom and an open, raised work bucket.
            n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.34f,BottomRadius=0.4f,Height=0.45f,RadialSegments=12},Position=new(front-2.65f,1.9f,0),MaterialOverride=Dark});
            Bar(n,new(front-2.65f,2.14f,0),new(back+1,2.55f,0),0.16f,paint);
            Bar(n,new(back+1,2.55f,0),new(front-1.8f,2.92f,0),0.12f,White);
            Bar(n,new(front-2.55f,2.05f,-0.2f),new(back+1.3f,2.5f,-0.2f),0.065f,Dark);
            float bucket=back+0.65f;
            Box(n,new(bucket,2.53f,0),new(0.82f,0.12f,0.95f),White);
            foreach(int side in new[]{-1,1})Box(n,new(bucket,2.85f,side*0.45f),new(0.82f,0.6f,0.07f),White);
            foreach(float x in new[]{bucket-0.38f,bucket+0.38f})Box(n,new(x,2.85f,0),new(0.07f,0.6f,0.9f),White);
            var reel=new Node3D{Position=new(deckX+0.25f,1.75f,0)};n.AddChild(reel);
            reel.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.31f,BottomRadius=0.31f,Height=0.5f,RadialSegments=16},RotationDegrees=new(90,0,0),MaterialOverride=Dark});
            foreach(int side in new[]{-1,1})reel.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.39f,BottomRadius=0.39f,Height=0.055f,RadialSegments=12},Position=new(0,0,side*0.28f),RotationDegrees=new(90,0,0),MaterialOverride=paint});
            for(int i=0;i<7;i++)
            {
                float z=-w*0.4f+i*w*0.13f;
                n.AddChild(new MeshInstance3D{Mesh=new BoxMesh{Size=new(0.035f,0.46f,0.13f)},Position=new(back-0.045f,1.44f,z),RotationDegrees=new(28,0,0),MaterialOverride=Dark});
            }
            Beacon(n,front-1.05f,2.9f,w,new[]{"Amber","Amber"});
        }
        private static void BuildCraneBody(Node3D n,float deckX,float deckL,float back,float front,float w,Material paint)
        {
            Box(n,new(deckX,1.27f,0),new(deckL,0.55f,w*0.95f),paint);
            foreach(int side in new[]{-1,1})
            {
                Box(n,new(deckX,1.22f,side*w*0.49f),new(deckL,0.17f,0.04f),Dark);
                foreach(float x in new[]{back+1.7f,front-3.2f})
                {
                    Box(n,new(x,0.92f,side*w*0.37f),new(0.58f,0.47f,w*0.26f),Dark);
                    Bar(n,new(x,0.6f,side*w*0.44f),new(x,1.5f,side*w*0.44f),0.105f,Alloy);
                    Bar(n,new(x,1.05f,side*w*0.44f),new(x,1.55f,side*w*0.44f),0.16f,paint);
                    Box(n,new(x,0.54f,side*w*0.44f),new(0.58f,0.13f,0.48f),Dark);
                }
            }
            float pivot=back+3.4f;
            n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.95f,BottomRadius=0.95f,Height=0.22f,RadialSegments=16},Position=new(pivot,1.65f,0),MaterialOverride=Dark});
            Round(n,new(pivot-0.5f,2.1f,0),new(2.9f,0.85f,2.2f),0.08f,paint);
            Box(n,new(pivot-1.4f,2.35f,0),new(0.85f,1.1f,2.25f),Dark);
            Round(n,new(pivot+0.5f,2.57f,-0.77f),new(1.45f,1.55f,1.05f),0.08f,paint);
            Box(n,new(pivot+1.24f,2.75f,-0.77f),new(0.025f,0.88f,0.84f),Glass);
            Box(n,new(pivot+0.5f,2.78f,-1.305f),new(1.12f,0.9f,0.025f),Glass);
            float boomStart=pivot-0.25f,boomEnd=front+0.75f;
            for(int i=0;i<4;i++)
            {
                float start=boomStart+i*(boomEnd-boomStart)*0.13f,end=boomEnd-(3-i)*0.32f;
                Round(n,new((start+end)/2,3.35f,0.22f),new(end-start,0.66f-i*0.12f,0.78f-i*0.14f),0.04f,i==3?Dark:paint);
                Box(n,new(end-0.08f,3.35f,0.22f),new(0.14f,0.73f-i*0.12f,0.85f-i*0.14f),Dark);
            }
            foreach(int side in new[]{-1,1})
            {
                Vector3 a=new Vector3(pivot-0.1f,1.92f,side*0.7f),b=new Vector3(pivot+2.5f,3.23f,side*0.5f);
                Bar(n,a,a.Lerp(b,0.62f),0.14f,paint);Bar(n,a.Lerp(b,0.58f),b,0.085f,Alloy);
            }
            float hookX=boomEnd-0.08f;
            Bar(n,new(hookX,3.56f,.06f),new(hookX,3.56f,.38f),0.12f,Dark);
            foreach(float z in new[]{.12f,.32f})Bar(n,new(hookX,3.53f,z),new(hookX,2.6f,z),0.025f,Dark);
            Box(n,new(hookX,2.48f,.22f),new(0.36f,0.38f,0.34f),paint);
            for(int i=0;i<6;i++)
            {
                float a=Mathf.DegToRad(90+i*43),b=Mathf.DegToRad(90+(i+1)*43);
                Bar(n,new(hookX+0.3f*Mathf.Cos(a),2.05f+0.3f*Mathf.Sin(a),.22f),new(hookX+0.3f*Mathf.Cos(b),2.05f+0.3f*Mathf.Sin(b),.22f),0.085f,Dark);
            }
            Beacon(n,front-1.05f,2.9f,w,new[]{"Amber","Amber"});
            foreach(int side in new[]{-1,1})AmberMarker(n,new(back+0.3f,1.6f,side*w*0.49f),new(0.34f,0.14f,0.12f));
        }
    }
}
