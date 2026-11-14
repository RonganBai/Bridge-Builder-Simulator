using Godot;

namespace BridgeArchitect3D.Traffic
{
    public static partial class SpecialVehicleFactory
    {
        private static void BuildAerialLadder(Node3D n,float back,float deckFront,float w)
        {
            float center=(back+deckFront)/2,length=deckFront-back-0.65f;
            n.AddChild(new MeshInstance3D{Mesh=new CylinderMesh{TopRadius=0.58f,BottomRadius=0.65f,Height=0.2f,RadialSegments=16},Position=new(center,2.86f,0),MaterialOverride=Dark});
            Box(n,new(center,2.98f,0),new(1.2f,0.15f,1.35f),Red);
            for(int stage=0;stage<3;stage++)
            {
                float half=0.62f-stage*0.14f,y=3.08f+stage*0.13f,len=length-stage*0.35f;
                foreach(int side in new[]{-1,1})
                {
                    Box(n,new(center,y,side*half),new(len,0.11f,0.07f),White);
                    Box(n,new(center,y+0.12f,side*half),new(len,0.055f,0.055f),Alloy);
                    for(float x=center-len/2+0.15f;x<center+len/2;x+=0.42f)
                        Box(n,new(x,y+0.06f,side*half),new(0.045f,0.14f,0.045f),Alloy);
                }
                for(float x=center-len/2+0.15f;x<center+len/2;x+=0.36f)
                    Box(n,new(x,y,0),new(0.05f,0.055f,half*2),Alloy);
            }
            float tip=center+(length-0.7f)/2;
            Box(n,new(tip,3.13f,0),new(0.65f,0.08f,1.0f),Alloy);
            foreach(int side in new[]{-1,1})Box(n,new(tip,3.36f,side*0.48f),new(0.65f,0.4f,0.055f),White);
            foreach(float x in new[]{tip-0.3f,tip+0.3f})Box(n,new(x,3.36f,0),new(0.055f,0.4f,1),White);
            foreach(int side in new[]{-1,1})
            {
                var position=new Vector3(back+0.3f,2.96f,side*(w*0.43f));
                Box(n,position-new Vector3(0,0.08f,0),new(0.48f,0.12f,0.35f),Dark);
                foreach(int colorIndex in new[]{0,1})
                {
                    string name="EmergencyBeacon"+(colorIndex==0?"Red":"White");
                    var color=new Color(BridgeArchitect3D.Data.EmergencyBeaconPattern.ColorFor(name));
                    var pos=position+new Vector3((colorIndex-0.5f)*0.23f,0.05f,0);
                    Box(n,pos,new(0.21f,0.16f,0.26f),new StandardMaterial3D{ResourceName=name,AlbedoColor=color});
                    n.AddChild(new OmniLight3D{Name=name+"RearLight"+n.GetChildCount(),Position=pos+Vector3.Up*0.15f,LightColor=color,LightEnergy=2.5f,OmniRange=4.5f,ShadowEnabled=false,Visible=false});
                }
            }
        }
        private static void BuildTrailerAdvertisements(Node3D trailer,float center,float length,float w)
        {
            string[] brands={"NORTHLINE","BRIDGE ENERGY","FRESH VALLEY","LIFTWORKS"};
            string[] slogans={"LOGISTICS  /  KEEPING CITIES MOVING","POWER FOR EVERY CONNECTION","FARM FRESH  /  DELIVERED DAILY","BUILD BIG  /  MOVE FORWARD"};
            string[] colors={"245875","bd782d","527755","4c597a"};
            for(int variant=0;variant<brands.Length;variant++)
            {
                var root=new Node3D{Name="Advertisement"+variant,Visible=variant==0};root.SetMeta("vehicle_variant",true);trailer.AddChild(root);
                var paint=new StandardMaterial3D{AlbedoColor=new Color(colors[variant]),Roughness=0.7f};
                foreach(int side in new[]{-1,1})
                {
                    float z=side*(w/2+0.045f);
                    Box(root,new(center,2.72f,z),new(length*0.8f,1.85f,0.035f),paint);
                    Box(root,new(center,1.91f,z+side*0.025f),new(length*0.76f,0.06f,0.025f),White);
                    Box(root,new(center-length*0.34f,2.77f,z+side*0.025f),new(0.19f,1.15f,0.025f),White);
                    void Text(string value,float y,int font,float pixel)
                    {
                        var label=new Label3D{Text=value,FontSize=font,PixelSize=pixel,Position=new(center,y,z+side*0.04f),RotationDegrees=new(0,side>0?0:180,0),Modulate=new Color("f5f0dd"),OutlineSize=0,Shaded=true,DoubleSided=false};
                        label.SetMeta("vehicle_advertisement",true);root.AddChild(label);
                    }
                    Text(brands[variant],2.99f,72,0.014f);
                    Text(slogans[variant],2.32f,32,0.012f);
                }
            }
        }
    }
}
