using System;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.World;
using BridgeArchitect3D.Building;
using Godot;

namespace BridgeArchitect3D.Traffic
{
    public static class SpecialVehicleFactory
    {
        public static bool Supports(string kind)=>kind is "suv" or "motorcycle" or "pickup" or "semi" or "ambulance";
        private static readonly StandardMaterial3D Dark=new(){AlbedoColor=new Color("25333a"),Roughness=0.8f};
        private static readonly StandardMaterial3D Glass=new(){AlbedoColor=new Color("709cae"),Roughness=0.2f,Metallic=0.2f};
        private static readonly StandardMaterial3D White=new(){AlbedoColor=new Color("dddeda"),Roughness=0.6f};
        private static readonly StandardMaterial3D Alloy=new(){AlbedoColor=new Color("82929b"),Metallic=0.7f,Roughness=0.35f};
        private static readonly StandardMaterial3D Red=new(){AlbedoColor=new Color("c94643"),Roughness=0.6f};
        private static readonly StandardMaterial3D Lamp=new(){AlbedoColor=new Color("f4d99e"),EmissionEnabled=true,Emission=new Color("bda276"),EmissionEnergyMultiplier=0.3f};
        private static void Box(Node3D n,Vector3 p,Vector3 s,Material m)=>LowPolyDetails.Box(n,p,s,m);
        private static void Round(Node3D n,Vector3 p,Vector3 s,float radius,Material m)=>n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.RoundedBox(s,radius),Position=p,MaterialOverride=m});
        private static void Wheel(Node3D n,float x,float y,float z,float width)
        {
            n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=y,BottomRadius=y,Height=width,RadialSegments=12,Rings=1},Position=new(x,y,z),RotationDegrees=new(90,0,0),MaterialOverride=Dark});
            n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=y*0.53f,BottomRadius=y*0.53f,Height=width+0.015f,RadialSegments=8,Rings=1},Position=new(x,y,z),RotationDegrees=new(90,0,0),MaterialOverride=Alloy});
        }
        private static void Bar(Node3D n,Vector3 a,Vector3 b,float radius,Material material)
        {
            var mesh=new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=radius,BottomRadius=radius,Height=a.DistanceTo(b),RadialSegments=7,Rings=1},MaterialOverride=material,Position=(a+b)/2};
            var up=(b-a).Normalized();var right=up.Cross(Vector3.Back).Normalized();mesh.Basis=new Basis(right,up,right.Cross(up).Normalized());n.AddChild(mesh);
        }
        public static void Build(VehicleDefinition def,Func<float,float,Node3D> unit)
        {
            var paint=new StandardMaterial3D{AlbedoColor=MaterialManager.ParseColor(def.Color),Roughness=0.5f,Metallic=0.15f};
            float w=(float)def.Width,l=(float)def.Length,h=(float)def.Height,rear=(float)def.Axles.Min(a=>a.Offset),front=(l+rear)/2;
            if(def.Kind=="motorcycle")
            {
                var bike=unit(0,rear);Wheel(bike,0,0.34f,0,0.14f);Wheel(bike,rear,0.34f,0,0.17f);
                Bar(bike,new(0,0.34f,-0.08f),new(-0.15f,0.98f,-0.08f),0.035f,Alloy);Bar(bike,new(0,0.34f,0.08f),new(-0.15f,0.98f,0.08f),0.035f,Alloy);
                Bar(bike,new(rear,0.34f,0),new(-0.4f,0.75f,0),0.055f,Dark);Bar(bike,new(-1,0.75f,0),new(-0.35f,0.72f,0),0.055f,Alloy);
                Round(bike,new(-0.45f,0.83f,0),new(0.6f,0.33f,0.35f),0.14f,paint);Round(bike,new(-1.02f,0.84f,0),new(0.65f,0.15f,0.32f),0.1f,Dark);
                Box(bike,new(-0.15f,1.02f,0),new(0.08f,0.05f,0.76f),Dark);Round(bike,new(0.08f,0.87f,0),new(0.09f,0.23f,0.24f),0.06f,Lamp);
                Round(bike,new(-0.83f,1.13f,0),new(0.3f,0.5f,0.4f),0.15f,paint);
                bike.AddChild(new MeshInstance3D{Mesh=new SphereMesh{Radius=0.16f,Height=0.3f,RadialSegments=8,Rings=4},Position=new(-0.61f,1.41f,0),MaterialOverride=White});
                Box(bike,new(-0.48f,1.43f,0),new(0.04f,0.1f,0.25f),Glass);
                foreach(int side in new[]{-1,1}){Bar(bike,new(-0.77f,1.24f,side*0.22f),new(-0.16f,1.04f,side*0.3f),0.055f,Dark);Bar(bike,new(-1,0.86f,side*0.2f),new(-0.55f,0.5f,side*0.28f),0.07f,Dark);}
                return;
            }
            if(def.Kind=="suv"){SuvVehicleFactory.Build(def,unit,paint);return;}
            if(def.Kind=="semi"){BuildSemi(def,unit,paint);return;}
            if(def.Kind=="ambulance"){BuildAmbulance(def,unit,paint);return;}
            var n=unit(0,rear);float back=rear-front,cx=rear/2;
            Round(n,new(cx,0.8f,0),new(l,0.65f,w),0.28f,paint);Box(n,new(cx,0.5f,0),new(l*0.9f,0.16f,w*0.86f),Dark);
            bool pickup=def.Kind=="pickup";
            float cabinLength=pickup?l*0.47f:l*0.75f,cabinCenter=pickup?front-cabinLength/2-0.15f:cx-0.1f,roof=h-0.13f;
            var profile=new[]{new Vector2(cabinCenter-cabinLength/2,0.95f),new Vector2(cabinCenter+cabinLength/2,0.95f),new Vector2(cabinCenter+cabinLength/2-0.35f,roof),new Vector2(cabinCenter-cabinLength/2+0.24f,roof)};
            n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(profile,w*0.91f),MaterialOverride=paint});
            CurvedMeshes.Windshield(n,cabinCenter+cabinLength/2+0.015f,1.07f,cabinCenter+cabinLength/2-0.35f,roof-0.08f,w*0.84f,0.08f,Glass);
            CurvedMeshes.Windshield(n,cabinCenter-cabinLength/2-0.015f,1.07f,cabinCenter-cabinLength/2+0.24f,roof-0.08f,w*0.84f,-0.06f,Glass);
            foreach(int side in new[]{-1,1})
            {
                Box(n,new(cabinCenter,1.16f+(roof-1.16f)/2,side*w*0.46f),new(cabinLength*0.7f,Math.Max(0.15f,roof-1.3f),0.025f),Glass);
                Box(n,new(cabinCenter,1.2f,side*w*0.465f),new(0.07f,Math.Max(0.2f,roof-1.1f),0.03f),Dark);
                Box(n,new(cabinCenter+0.3f,1.02f,side*w*0.5f),new(0.27f,0.04f,0.025f),Alloy);
                Box(n,new(front+0.02f,0.9f,side*w*0.34f),new(0.06f,0.2f,0.42f),Lamp);Box(n,new(back-0.01f,0.86f,side*w*0.37f),new(0.04f,0.2f,0.2f),Red);
                if(!pickup)Box(n,new(cx,roof+0.06f,side*w*0.34f),new(cabinLength*0.84f,0.07f,0.07f),Dark);
                foreach(var axle in def.Axles)Wheel(n,(float)axle.Offset,0.43f,side*(w/2-0.12f),0.28f);
            }
            Box(n,new(front+0.02f,0.73f,0),new(0.05f,0.22f,w*0.46f),Dark);
            if(pickup)
            {
                float bedFront=cabinCenter-cabinLength/2-0.1f,bedLength=bedFront-back,bedCenter=(back+bedFront)/2;
                Box(n,new(bedCenter,1.15f,0),new(bedLength-0.14f,0.08f,w*0.87f),Dark);
                foreach(int side in new[]{-1,1})Box(n,new(bedCenter,1.325f,side*w*0.46f),new(bedLength-0.05f,0.43f,0.13f),paint);
                Box(n,new(back+0.1f,1.325f,0),new(0.16f,0.43f,w*0.9f),paint);
                Box(n,new(bedFront-0.06f,1.325f,0),new(0.12f,0.43f,w*0.9f),paint);
            }

        }

        private static void SideWindow(Node3D parent,Vector2[] profile,float z,Material material)
            =>parent.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(profile,0.025f),Position=new Vector3(0,0,z),MaterialOverride=material});

        private static void BuildSemi(VehicleDefinition def,Func<float,float,Node3D> unit,Material paint)
        {
            float w=(float)def.Width,l=(float)def.Length,rear=(float)def.Axles.Min(a=>a.Offset),front=(l+rear)/2;
            const float trailerFront=-4.8f;float cargoLength=l-front+trailerFront,cargoCenter=-cargoLength/2;
            var cab=unit(0,-4.9f);var trailer=unit(trailerFront,rear-front);
            var subtle=new StandardMaterial3D{AlbedoColor=new Color("c6cecd"),Roughness=0.7f};
            Box(cab,new(-1.6f,0.84f,0),new(7,0.28f,w*0.65f),Dark);
            var profile=new[]{new Vector2(front-3.7f,0.8f),new Vector2(front,0.8f),new Vector2(front+0.05f,1.9f),new Vector2(front-0.01f,2.8f),new Vector2(front-0.17f,3.52f),new Vector2(front-0.55f,3.82f),new Vector2(front-3.7f,3.82f)};
            cab.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(profile,w*0.94f),MaterialOverride=paint});
            CurvedMeshes.Windshield(cab,front+0.025f,2.35f,front-0.13f,3.42f,w*0.84f,0.04f,Glass);
            CurvedMeshes.Windshield(cab,front+0.031f,2.35f,front-0.124f,3.42f,0.055f,0.04f,Dark);
            Round(cab,new(-0.95f,3.9f,0),new(2.1f,0.25f,w*0.85f),0.16f,paint);
            Box(cab,new(front+0.09f,1.83f,0),new(0.06f,0.56f,w*0.65f),Dark);
            for(int i=0;i<4;i++)Box(cab,new(front+0.125f,1.64f+i*0.13f,0),new(0.025f,0.035f,w*0.6f),Alloy);
            Box(cab,new(front+0.1f,0.9f,0),new(0.18f,0.23f,w*0.94f),Alloy);
            foreach(int side in new[]{-1,1})
            {
                var window=new[]{new Vector2(front-2.25f,2.3f),new Vector2(front-0.45f,2.3f),new Vector2(front-0.6f,3.3f),new Vector2(front-2.25f,3.3f)};
                SideWindow(cab,window,side*w*0.475f,Glass);
                Box(cab,new(front-3.15f,2.95f,side*w*0.475f),new(0.48f,0.42f,0.025f),Glass);
                Box(cab,new(front-2.4f,2.1f,side*w*0.478f),new(0.025f,1.72f,0.02f),Dark);
                Box(cab,new(front-1.9f,2.05f,side*w*0.48f),new(0.32f,0.055f,0.03f),Alloy);
                Box(cab,new(front-0.3f,2.7f,side*w*0.488f),new(0.12f,0.46f,0.055f),Dark);
                Box(cab,new(front+0.13f,1.24f,side*w*0.33f),new(0.065f,0.24f,0.44f),Lamp);
                Box(cab,new(front-1.1f,1.14f,side*w*0.47f),new(1.2f,0.12f,0.18f),Alloy);
                Box(cab,new(-3.3f,0.92f,side*w*0.32f),new(1.5f,0.48f,0.45f),Alloy);
            }
            cab.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.58f,BottomRadius=0.58f,Height=0.12f,RadialSegments=10,Rings=1},Position=new(-4.25f,1.02f,0),MaterialOverride=Dark});
            Round(trailer,new(cargoCenter,2.65f,0),new(cargoLength,2.8f,w),0.08f,White);
            Box(trailer,new(cargoCenter,1.12f,0),new(cargoLength,0.24f,w*0.68f),Dark);
            Box(trailer,new(cargoCenter,1.26f,0),new(cargoLength,0.12f,w*0.98f),Alloy);
            for(int i=0;i<12;i++)foreach(int side in new[]{-1,1})
            {
                float x=-0.5f-i*(cargoLength-1)/11;
                Box(trailer,new(x,2.65f,side*w*0.502f),new(0.035f,2.55f,0.015f),subtle);
                if(i%2==0)Box(trailer,new(x,1.4f,side*w*0.507f),new(0.42f,0.07f,0.02f),Red);
            }
            foreach(int side in new[]{-1,1})
            {
                Box(trailer,new(-cargoLength-0.012f,2.65f,side*w*0.225f),new(0.025f,2.55f,w*0.42f),White);
                Box(trailer,new(-cargoLength-0.035f,2.6f,side*w*0.13f),new(0.025f,2.3f,0.04f),Alloy);
                Box(trailer,new(-cargoLength-0.04f,1.12f,side*w*0.34f),new(0.04f,0.2f,0.3f),Red);
            }
            Box(trailer,new(-cargoLength-0.045f,2.65f,0),new(0.02f,2.58f,0.025f),Dark);
            Box(trailer,new(-cargoLength,0.68f,0),new(0.12f,0.14f,w*0.86f),Alloy);
            foreach(var axle in def.Axles)foreach(int side in new[]{-1,1})
            {
                bool tractor=axle.Offset>-6;float x=(float)(tractor?axle.Offset:axle.Offset-trailerFront);var parent=tractor?cab:trailer;
                Wheel(parent,x,0.49f,side*(w/2-0.17f),0.35f);
                if(!tractor)Box(parent,new(x-0.55f,0.47f,side*(w/2-0.16f)),new(0.05f,0.65f,0.4f),Dark);
            }
            foreach(var axle in def.Axles)
            {
                bool tractor=axle.Offset>-6;float x=(float)(tractor?axle.Offset:axle.Offset-trailerFront);var parent=tractor?cab:trailer;
                parent.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.07f,BottomRadius=0.07f,Height=w-0.25f,RadialSegments=8,Rings=1},Position=new(x,0.49f,0),RotationDegrees=new(90,0,0),MaterialOverride=Dark});
            }
        }

        private static void BuildAmbulance(VehicleDefinition def,Func<float,float,Node3D> unit,Material paint)
        {
            float w=(float)def.Width,l=(float)def.Length,rear=(float)def.Axles.Min(a=>a.Offset),front=(l+rear)/2,back=rear-front;
            var n=unit(0,rear);float medicalFront=front-2.05f,boxLength=medicalFront-back,boxCenter=(medicalFront+back)/2;
            var blue=new StandardMaterial3D{ResourceName="AmbulanceBeaconBlue",AlbedoColor=new Color("467ca8"),Roughness=0.3f};
            var beaconRed=new StandardMaterial3D{ResourceName="AmbulanceBeaconRed",AlbedoColor=new Color("d94d43"),Roughness=0.3f};
            Box(n,new(rear/2,0.5f,0),new(l*0.95f,0.2f,w*0.84f),Dark);
            var cabProfile=new[]{new Vector2(front-2.2f,0.55f),new Vector2(front,0.55f),new Vector2(front+0.04f,0.85f),new Vector2(front-0.02f,1.12f),new Vector2(front-0.7f,1.32f),new Vector2(front-1.2f,2.2f),new Vector2(front-2.2f,2.25f)};
            n.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.Extrude(cabProfile,w*0.88f),MaterialOverride=paint});
            Round(n,new(boxCenter,1.6f,0),new(boxLength,2.1f,w*0.96f),0.12f,paint);
            CurvedMeshes.Windshield(n,front-0.69f,1.37f,front-1.18f,2.16f,w*0.8f,0.06f,Glass);
            foreach(int side in new[]{-1,1})
            {
                var window=new[]{new Vector2(front-2.02f,1.42f),new Vector2(front-0.9f,1.42f),new Vector2(front-1.28f,2.12f),new Vector2(front-2.02f,2.12f)};
                SideWindow(n,window,side*w*0.445f,Glass);
                Box(n,new(front-1.75f,1.22f,side*w*0.447f),new(0.23f,0.045f,0.03f),Dark);
                Box(n,new(front-0.72f,1.7f,side*w*0.48f),new(0.1f,0.27f,0.06f),Dark);
                Box(n,new(boxCenter,0.89f,side*w*0.485f),new(boxLength*0.94f,0.11f,0.025f),Red);
                float doorX=medicalFront-0.72f;
                foreach(float edge in new[]{doorX-0.48f,doorX+0.48f})Box(n,new(edge,1.47f,side*w*0.486f),new(0.022f,1.53f,0.02f),Alloy);
                Box(n,new(doorX,2.24f,side*w*0.486f),new(0.98f,0.022f,0.02f),Alloy);
                Box(n,new(doorX,2,side*w*0.49f),new(0.62f,0.34f,0.025f),Glass);
                Box(n,new(doorX+0.3f,1.38f,side*w*0.49f),new(0.045f,0.27f,0.025f),Dark);
                float logoX=back+0.9f;
                Box(n,new(logoX,2.14f,side*w*0.49f),new(0.85f,0.38f,0.025f),Glass);
                Box(n,new(logoX,1.38f,side*w*0.49f),new(0.55f,0.14f,0.03f),Red);
                Box(n,new(logoX,1.38f,side*w*0.492f),new(0.14f,0.55f,0.03f),Red);
                Box(n,new(medicalFront-0.1f,2.66f,side*w*0.2f),new(0.28f,0.08f,w*0.3f),side>0?beaconRed:blue);
                n.AddChild(new OmniLight3D {Name=side>0?"AmbulanceBeaconRedLight":"AmbulanceBeaconBlueLight",Position=new(medicalFront-0.1f,2.76f,side*w*0.2f),LightColor=new Color(side>0?"ef5147":"418ff0"),LightEnergy=2.5f,OmniRange=4.5f,ShadowEnabled=false,Visible=false});
                Box(n,new(back-0.014f,1.52f,side*w*0.205f),new(0.025f,1.68f,w*0.37f),White);
                Box(n,new(back-0.034f,2.1f,side*w*0.205f),new(0.025f,0.42f,w*0.25f),Glass);
                Box(n,new(back-0.05f,1.26f,side*w*0.205f),new(0.02f,0.12f,0.36f),Red);
                Box(n,new(back-0.055f,1.26f,side*w*0.205f),new(0.02f,0.36f,0.12f),Red);
                Box(n,new(back-0.04f,0.97f,side*w*0.43f),new(0.045f,0.3f,0.14f),Red);
                Box(n,new(front+0.06f,0.98f,side*w*0.3f),new(0.045f,0.18f,0.34f),Lamp);
                foreach(var axle in def.Axles)Wheel(n,(float)axle.Offset,0.43f,side*(w/2-0.16f),0.28f);
            }
            Box(n,new(back-0.035f,1.6f,0),new(0.02f,1.86f,0.03f),Alloy);
            Box(n,new(front+0.075f,0.65f,0),new(0.12f,0.17f,w*0.9f),Dark);
            Box(n,new(front+0.035f,0.99f,0),new(0.04f,0.22f,w*0.36f),Dark);
            Box(n,new(back,0.61f,0),new(0.15f,0.14f,w*0.88f),Dark);
        }
    }
}
