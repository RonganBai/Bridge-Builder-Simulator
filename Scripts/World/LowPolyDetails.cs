using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using Godot;

namespace BridgeArchitect3D.World
{
    public static class LowPolyDetails
    {
        private static readonly BoxMesh Cube=new(){Size=Vector3.One};
        private static readonly StandardMaterial3D Paint=new(){AlbedoColor=new Color("e4e6df"),Roughness=0.55f};
        private static readonly StandardMaterial3D Ink=new(){AlbedoColor=new Color("242f38"),Roughness=0.8f};
        private static readonly StandardMaterial3D Alloy=new(){AlbedoColor=new Color("9caeb2"),Metallic=0.65f,Roughness=0.35f};
        private static readonly StandardMaterial3D Amber=new(){AlbedoColor=new Color("eea834"),Roughness=0.7f};
        public static void Box(Node3D parent,Vector3 position,Vector3 size,Material material)
            =>parent.AddChild(new MeshInstance3D{Mesh=Cube,Position=position,Scale=size,MaterialOverride=material});

        public static void DressVehicle(Node3D root,VehicleDefinition def,int unit)
        {
            float w=(float)def.Width,h=(float)def.Height,l=(float)def.Length;
            float rear=(float)def.Axles.Select(a=>a.Offset).DefaultIfEmpty(0).Min();
            float front=Math.Max(0.3f,(l+rear)/2),cx=rear/2;
            void B(float x,float y,float z,float sx,float sy,float sz,Material material)=>Box(root,new(x,y,z),new(sx,sy,sz),material);
            switch(def.Kind)
            {
                case "car":
                    root.AddChild(new MeshInstance3D{Mesh=CurvedMeshes.RoundedBox(new Vector3(l*0.35f,0.08f,w*0.87f),0.12f),Position=new Vector3(cx-0.15f,1.47f,0),MaterialOverride=Paint});
                    B(front+0.065f,0.89f,0,0.03f,0.18f,w*0.28f,Ink);
                    B(front+0.07f,0.62f,0,0.03f,0.12f,0.42f,Paint);
                    foreach(int side in new[]{-1,1})
                    {
                        B(cx-0.12f,1.3f,side*w*0.445f,0.09f,0.42f,0.03f,Ink);
                        B(cx-0.55f,0.99f,side*w*0.503f,0.28f,0.035f,0.025f,Alloy);
                        B(cx+0.35f,0.99f,side*w*0.503f,0.25f,0.035f,0.025f,Alloy);
                        B(cx,0.58f,side*w*0.505f,l*0.66f,0.055f,0.025f,Ink);
                        B(cx-0.7f,1.28f,side*w*0.447f,0.045f,0.42f,0.025f,Paint);
                    }
                    break;
                case "truck":
                    float back=rear-front,boxLength=l-2.6f;
                    foreach(int side in new[]{-1,1})
                    {
                        B(front-1.4f,1.32f,side*w*0.505f,0.28f,0.045f,0.025f,Alloy);
                        B(front-1.9f,0.75f,side*w*0.51f,0.7f,0.12f,0.12f,Alloy);
                        B(front-2.6f-boxLength/2,1.1f,side*w*0.51f,boxLength*0.92f,0.09f,0.025f,Amber);
                        foreach(float offset in new[]{0.3f,0.7f})B(back-0.015f,h*offset,side*w*0.38f,0.035f,0.16f,0.18f,Alloy);
                        foreach(var axle in def.Axles)B((float)axle.Offset-0.5f,0.37f,side*(w/2-0.1f),0.06f,0.42f,0.38f,Ink);
                    }
                    B(back-0.02f,h*0.52f,0,0.025f,h*0.66f,0.045f,Ink);
                    for(int i=0;i<3;i++)B(front+0.065f,1.06f+i*0.13f,0,0.025f,0.035f,w*0.62f,Alloy);
                    B(front-1.2f,2.55f,0,0.45f,0.12f,0.65f,Paint);
                    break;
                case "bus":
                    for(int i=0;i<7;i++)foreach(int side in new[]{-1,1})
                        B(front-l*0.12f-i*l*0.115f,0.45f+h*0.66f,side*w*0.509f,0.07f,h*0.32f,0.025f,Paint);
                    foreach(int side in new[]{-1,1})
                    {
                        B(cx,1.08f,side*w*0.51f,l*0.98f,0.12f,0.025f,Paint);
                        for(int i=0;i<4;i++)B(rear-front+0.5f,0.95f+i*0.14f,side*w*0.51f,0.7f,0.035f,0.025f,Ink);
                    }
                    B(front+0.04f,0.45f+h*0.92f,0,0.025f,0.14f,w*0.4f,Ink);
                    B(front+0.09f,0.7f,0,0.025f,0.12f,0.55f,Paint);
                    break;
                case "train":
                    float ul=(float)(def.UnitLength>0?def.UnitLength:l/Math.Max(1,def.Units)),cl=ul-0.6f;
                    foreach(int side in new[]{-1,1})
                    {
                        B(-cl/2,1.12f,side*w*0.507f,cl*0.96f,0.15f,0.025f,Amber);
                        for(int i=0;i<(unit==0?8:6);i++)B(-3-i*(cl-4)/8,unit==0?2.45f:2.7f,side*w*0.512f,unit==0?0.11f:0.045f,unit==0?1.1f:2.25f,0.025f,unit==0?Ink:Alloy);
                        B(-0.7f,1.15f,side*w*0.51f,0.5f,0.1f,0.22f,Alloy);
                        B(-cl+0.5f,1.15f,side*w*0.51f,0.5f,0.1f,0.22f,Alloy);
                    }
                    if(unit==0){B(-4.5f,4.14f,0,2,0.12f,w*0.65f,Ink);B(-1,3.6f,0,0.06f,0.8f,w*1.02f,Paint);}
                    else B(-cl/2,4.16f,0,cl*0.88f,0.08f,w*0.8f,Alloy);
                    break;
                case "tank":
                    for(int i=0;i<5;i++)foreach(int side in new[]{-1,1})
                    {
                        B(1.5f-i*1.03f,1.05f,side*w*0.46f,0.9f,0.45f,0.07f,Alloy);
                        B(1.5f-i*1.03f,1.17f,side*w*0.475f,0.08f,0.08f,0.025f,Ink);
                    }
                    B(-1.2f,2.36f,-0.4f,0.5f,0.12f,0.55f,Ink);
                    B(-2,2.32f,0.6f,0.025f,0.38f,0.025f,Ink);
                    for(int i=0;i<4;i++)B(-2.6f+i*0.22f,1.57f,0,0.08f,0.025f,w*0.48f,Ink);
                    break;
                case "pedestrians":
                    float z=unit%2==0?-0.6f:0.6f;
                    B(-0.23f,1.2f,z,0.16f,0.43f,0.35f,Amber);
                    B(0,1.75f,z,0.25f,0.08f,0.27f,Ink);
                    foreach(int side in new[]{-1,1})B(0,1.17f,z+side*0.25f,0.13f,0.47f,0.12f,Paint);
                    break;
            }
            // Keep the model within the vehicle's physical clearance height.
            float top=0;
            foreach(var mesh in root.GetChildren().OfType<MeshInstance3D>())
            {
                var bounds=mesh.Transform*mesh.GetAabb();top=Math.Max(top,bounds.End.Y);
            }
            if(top>h)foreach(var mesh in root.GetChildren().OfType<MeshInstance3D>())
            {var t=mesh.Transform;t.Origin=new Vector3(t.Origin.X,t.Origin.Y*h/top,t.Origin.Z);t.Basis=new Basis(t.Basis.X*new Vector3(1,h/top,1),t.Basis.Y*new Vector3(1,h/top,1),t.Basis.Z*new Vector3(1,h/top,1));mesh.Transform=t;}
            Batch(root);
        }

        /// <summary>One mesh per material and unit keeps added detail from multiplying draw calls.</summary>
        public static void Batch(Node3D root)
        {
            var meshes=new List<(MeshInstance3D mesh,Transform3D transform)>();
            void Collect(Node3D node,Transform3D transform)
            {
                foreach(var child in node.GetChildren().OfType<Node3D>())
                {
                    var local=transform*child.Transform;
                    if(child is MeshInstance3D mesh && mesh.Mesh!=null)meshes.Add((mesh,local));
                    else Collect(child,local);
                }
            }
            Collect(root,Transform3D.Identity);
            foreach(var group in meshes.GroupBy(m=>m.mesh.MaterialOverride))
            {
                var surface=new SurfaceTool();surface.Begin(Mesh.PrimitiveType.Triangles);
                int expectedFaces=0;
                foreach(var item in group)
                {
                    // Normalize every source: mixing indexed primitives and unindexed custom curves loses faces.
                    expectedFaces+=item.mesh.Mesh.GetFaces().Length;
                    var indexed=new SurfaceTool();indexed.CreateFrom(item.mesh.Mesh,0);indexed.Index();
                    surface.AppendFrom(indexed.Commit(),0,item.transform);
                }
                surface.Index();var combined=surface.Commit();
                if(combined.GetFaces().Length!=expectedFaces)
                {
                    GD.PushWarning("Mesh batching face-count mismatch; retaining the original visible meshes.");
                    continue;
                }
                root.AddChild(new MeshInstance3D{Mesh=combined,MaterialOverride=group.Key});
                foreach(var item in group){item.mesh.GetParent().RemoveChild(item.mesh);item.mesh.QueueFree();}
            }
        }
    }
}

