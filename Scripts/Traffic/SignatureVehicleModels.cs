using Godot;
using BridgeArchitect3D.Data;
using System.Linq;
using System.Collections.Generic;
using BridgeArchitect3D.World;

namespace BridgeArchitect3D.Traffic
{
    public static partial class SpecialVehicleFactory
    {
        private static void VehicleText(Node3D n,string text,Vector3 position,Vector3 rotation,float pixel=0.007f,int font=48,Color? color=null)
        {
            var label=new Label3D{Text=text,Position=position,RotationDegrees=rotation,PixelSize=pixel,FontSize=font,OutlineSize=0,Modulate=color??Colors.Black,Shaded=true,DoubleSided=false};
            label.SetMeta("vehicle_advertisement",true);n.AddChild(label);
        }
        private static void SchoolBus(Node3D n,VehicleDefinition d,float front,float back,float w,Material paint)
        {
            float bodyFront=front-2.1f,center=(back+bodyFront)/2,length=bodyFront-back,rear=(float)d.Axles.Min(a=>a.Offset),frontAxle=(float)d.Axles.Max(a=>a.Offset);
            var outline=new List<Vector2>{new(bodyFront-.18f,2.74f),new(back+0.2f,2.74f),new(back,2.5f),new(back,0.52f)};
            foreach(float axle in new[]{rear,frontAxle})
            {
                outline.Add(new(axle-0.6f,0.52f));
                for(int i=0;i<=12;i++){float a=Mathf.Pi-i*Mathf.Pi/12;outline.Add(new(axle+0.6f*Mathf.Cos(a),0.46f+0.6f*Mathf.Sin(a)));}
                outline.Add(new(axle+0.6f,0.52f));
            }
            // The forward wheel arch ends in the engine-bay fender rather than closing across the opening.
            outline.RemoveRange(outline.Count-15,15);
            outline.Add(new(frontAxle-0.6f,0.52f));
            float endAngle=Mathf.Acos(Mathf.Clamp((bodyFront-frontAxle)/0.6f,-1,1));
            for(int i=0;i<=12;i++){float a=Mathf.Lerp(Mathf.Pi,endAngle,i/12f);outline.Add(new(frontAxle+.6f*Mathf.Cos(a),.46f+.6f*Mathf.Sin(a)));}
            n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(outline.ToArray(),w*0.98f),MaterialOverride=paint});
            // Roof is a continuous arched cross section along the passenger compartment.
            var roof=new List<Vector2>{new(-w*.49f,2.64f)};
            for(int i=0;i<=12;i++){float a=Mathf.Pi-i*Mathf.Pi/12;roof.Add(new(w*.49f*Mathf.Cos(a),2.67f+.42f*Mathf.Sin(a)));}
            roof.Add(new(w*.49f,2.64f));
            n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(roof.AsEnumerable().Reverse().ToArray(),length),Position=new(center,0,0),RotationDegrees=new(0,90,0),MaterialOverride=paint});
            // Long sloping bonnet and a separate tall radiator nose.
            // A faceted crown rounds both shoulders without a high-resolution curved surface.
            float hoodHalf=w*.43f;
            var bonnet=new List<Vector2>{new(-hoodHalf,.74f),new(hoodHalf,.74f),new(hoodHalf,1.40f)};
            for(int i=0;i<=8;i++)
            {
                float angle=i*Mathf.Pi/8;
                bonnet.Add(new(hoodHalf*Mathf.Cos(angle),1.40f+.29f*Mathf.Sin(angle)));
            }
            var hood=new Node3D {Position=new((front+bodyFront)/2,0,0),RotationDegrees=new(0,0,-5)};
            n.AddChild(hood);
            hood.AddChild(new MeshInstance3D {Mesh=CurvedMeshes.Extrude(bonnet.ToArray(),front-bodyFront),RotationDegrees=new(0,90,0),MaterialOverride=paint});
            // Mount front details in the hood frame so pitch and thickness cannot bury them.
            float noseX=(front-bodyFront)/2;
            Box(hood,new(noseX+.025f,1.18f,0),new(.05f,.68f,w*.43f),Alloy);
            Box(hood,new(noseX+.055f,1.18f,0),new(.018f,.60f,w*.39f),Dark);
            for(int i=-4;i<=4;i++)Box(hood,new(noseX+.072f,1.18f,i*w*.042f),new(.018f,.55f,.028f),Alloy);
            Box(n,new(front+0.04f,0.58f,0),new(0.16f,0.24f,w*0.94f),Dark);
            CurvedMeshes.Windshield(n,bodyFront+.012f,1.85f,bodyFront-.158f,2.66f,w*.86f,0f,Glass);
            CurvedMeshes.Windshield(n,bodyFront+.018f,1.85f,bodyFront-.152f,2.66f,.055f,0f,Dark);
            VehicleText(n,"SCHOOL BUS",new(bodyFront+0.035f,2.84f,0),new(0,90,0),0.0065f,52);
            VehicleText(n,"SCHOOL BUS",new(back-0.035f,2.84f,0),new(0,-90,0),0.0065f,52);
            foreach(int side in new[]{-1,1})
            {
                for(float x=back+0.65f;x<bodyFront-0.55f;x+=0.95f)
                {
                    Box(n,new(x,2.21f,side*w*0.499f),new(0.77f,0.75f,0.025f),Dark);
                    Box(n,new(x,2.21f,side*w*0.512f),new(0.67f,0.64f,0.02f),Glass);
                    Box(n,new(x,2.21f,side*w*0.524f),new(0.69f,0.035f,0.015f),Dark);
                }
                foreach(float y in new[]{0.98f,1.22f,1.58f})Box(n,new(center,y,side*w*0.5f),new(length-.12f,0.065f,0.025f),Dark);
                for(int i=0;i<8;i++)
                {
                    float a=Mathf.Pi/8+i*Mathf.Pi/8,b=a+Mathf.Pi/8;
                    var p=new Vector3(frontAxle+.63f*Mathf.Cos(a),.46f+.63f*Mathf.Sin(a),side*w*.43f);
                    var q=new Vector3(frontAxle+.63f*Mathf.Cos(b),.46f+.63f*Mathf.Sin(b),side*w*.43f);
                    Bar(n,p,q,0.09f,paint);
                }
                hood.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=.18f,BottomRadius=.18f,Height=.05f,RadialSegments=12},Position=new(noseX+.03f,1.20f,side*w*.33f),RotationDegrees=new(0,0,90),MaterialOverride=Alloy});
                hood.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=.145f,BottomRadius=.145f,Height=.025f,RadialSegments=12},Position=new(noseX+.065f,1.20f,side*w*.33f),RotationDegrees=new(0,0,90),MaterialOverride=Lamp});
                Box(n,new(bodyFront+.25f,2.25f,side*w*.57f),new(.17f,.44f,.09f),Dark);
                foreach(float x in new[]{bodyFront+.04f,back-.04f})
                {
                    Box(n,new(x,2.88f,side*w*.34f),new(.05f,.18f,.25f),Red);
                    Box(n,new(x,2.88f,side*w*.19f),new(.05f,.18f,.2f),Lamp);
                }
                Box(n,new(back-.03f,1.16f,side*w*.37f),new(.04f,.26f,.16f),Red);
            }
            Box(n,new(bodyFront-.65f,1.48f,w*.514f),new(.75f,1.9f,.035f),Dark);
            foreach(float x in new[]{bodyFront-.85f,bodyFront-.45f})Box(n,new(x,1.72f,w*.53f),new(.3f,1.1f,.025f),Glass);
            Box(n,new(back-.035f,1.62f,0),new(.04f,1.8f,.82f),Dark);
            Box(n,new(back-.06f,2.0f,0),new(.025f,.7f,.66f),Glass);
            var sign=new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=.32f,BottomRadius=.32f,Height=.04f,RadialSegments=8},Position=new(bodyFront-1.35f,1.65f,-w*.52f),RotationDegrees=new(90,0,0),MaterialOverride=Red};n.AddChild(sign);
            VehicleText(n,"STOP",new(bodyFront-1.35f,1.65f,-w*.55f),new(0,180,0),.0055f,38,Colors.White);
        }
        private static void FarmWheel(Node3D n,float x,float radius,float z,float width,Material yellow)
        {
            n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=radius-.055f,BottomRadius=radius-.055f,Height=width,RadialSegments=20},Position=new(x,radius,z),RotationDegrees=new(90,0,0),MaterialOverride=Dark});
            float side=Mathf.Sign(z);
            n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=radius*.52f,BottomRadius=radius*.52f,Height=width+.025f,RadialSegments=16},Position=new(x,radius,z),RotationDegrees=new(90,0,0),MaterialOverride=yellow});
            for(int i=0;i<20;i++)
            {
                float a=i*Mathf.Tau/20;
                foreach(int half in new[]{-1,1})n.AddChild(new MeshInstance3D{Mesh=new BoxMesh{Size=new(radius*.22f,.09f,width*.58f)},Position=new(x+(radius-.025f)*Mathf.Cos(a),radius+(radius-.025f)*Mathf.Sin(a),z+half*width*.22f),RotationDegrees=new(0,half*28,Mathf.RadToDeg(a)-90),MaterialOverride=Dark});
            }
            Box(n,new(x,radius,z+side*(width/2+.025f)),new(radius*.32f,radius*.32f,.045f),Alloy);
        }
        private static void FarmTractor(Node3D n,float front,Material paint)
        {
            var yellow=new StandardMaterial3D{AlbedoColor=new Color("e2b834"),Roughness=.6f};
            Box(n,new(-.85f,.65f,0),new(4.2f,.3f,.85f),Dark);
            Round(n,new(front-1.05f,1.12f,0),new(2.08f,.86f,1.18f),.14f,paint);
            Box(n,new(front+.02f,1.12f,0),new(.04f,.61f,.91f),Dark);
            for(int i=-4;i<=4;i++)Box(n,new(front+.045f,1.12f,i*.09f),new(.025f,.53f,.026f),Alloy);
            foreach(int side in new[]{-1,1})
            {
                FarmWheel(n,0,.46f,side*.82f,.32f,yellow);FarmWheel(n,-2.5f,.85f,side*.93f,.53f,yellow);
                Box(n,new(-2.5f,1.73f,side*.96f),new(1.85f,.13f,.63f),paint);
                Box(n,new(-1.22f,.82f,side*.73f),new(.55f,.14f,.3f),Alloy);
                Box(n,new(-1.22f,1.03f,side*.69f),new(.45f,.11f,.25f),Alloy);
                Box(n,new(front-.94f,1.22f,side*.602f),new(1.7f,.085f,.025f),yellow);
                Box(n,new(front+.04f,1.37f,side*.35f),new(.04f,.18f,.22f),Lamp);
            }
            Box(n,new(-1.93f,1.1f,0),new(1.48f,.32f,1.38f),paint);
            Box(n,new(-1.93f,2.51f,0),new(1.72f,.14f,1.65f),paint);
            Box(n,new(-1.16f,1.94f,0),new(.035f,1.05f,1.25f),Glass);
            Box(n,new(-2.7f,1.94f,0),new(.035f,1.05f,1.25f),Glass);
            foreach(int side in new[]{-1,1})
            {
                Box(n,new(-1.93f,1.94f,side*.71f),new(1.34f,1.02f,.025f),Glass);
                foreach(float x in new[]{-2.67f,-1.2f})Box(n,new(x,1.93f,side*.72f),new(.08f,1.17f,.07f),Dark);
                Box(n,new(-1.3f,1.52f,side*.735f),new(.23f,.04f,.025f),Dark);
                Box(n,new(-1.06f,2.14f,side*.93f),new(.14f,.3f,.08f),Dark);
            }
            Bar(n,new(front-.55f,1.4f,-.53f),new(front-.55f,2.43f,-.53f),.075f,Dark);
            Bar(n,new(front-.55f,2.43f,-.53f),new(front-.78f,2.49f,-.53f),.075f,Dark);
            AmberMarker(n,new(-2.4f,2.65f,0),new(.2f,.17f,.2f));
            Bar(n,new(-2.8f,.62f,0),new(-3.15f,.56f,0),.13f,Alloy);
        }
    }
}
