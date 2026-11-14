using System;
using System.Collections.Generic;
using System.Linq;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.Building
{
    public partial class BridgeRenderer
    {
        private Node3D _reservedAccess;
        private TunnelTypeConfig _reservedType;
        private sealed class SlidingGate
        {
            public Node3D Left,Right;
            public Vector3 LeftClosed,RightClosed;
            public float Travel;
            public void SetOpen(float amount)
            {
                Left.Position=LeftClosed-Vector3.Back*Travel*amount;
                Right.Position=RightClosed+Vector3.Back*Travel*amount;
            }
        }
        private sealed class LiftVisual
        {
            public Node3D Platform,Counterweight;
            public float Bottom,Top,CounterOffset;
            public bool Down;
            public SlidingGate CageEntry,CageExit,LandingEntry,LandingExit;
            public readonly List<(MeshInstance3D mesh,Vector3 top,Vector3 attachment)> Ropes=new();
            public MeshInstance3D CounterRope;
            /// <summary>Built lift (members drawn with the structure): counterweights hang from the sheave nodes.</summary>
            public bool Structural;
            public readonly List<(Node3D weight,Vector3 sheave,float hang)> Weights=new();
            public readonly Dictionary<Node3D,MeshInstance3D> WeightRopes=new();
            public Vector3 CounterRopeTop;
        }
        private readonly List<LiftVisual> _liftPlatforms=new();

        public void ShowReservedAccess(TunnelTypeConfig type,bool visible)
        {
            if(type!=_reservedType)
            {
                _reservedAccess?.QueueFree();_reservedAccess=null;_reservedType=type;
                if(type!=null)
                {
                    _reservedAccess=new Node3D {Name="ReservedTunnelLifts"};AddChild(_reservedAccess);
                    var plan=new TunnelAccessPlan(Level,TunnelAccessPlan.Preview(Level,type));
                    AddLift(_reservedAccess,plan.Left,type,true,null);AddLift(_reservedAccess,plan.Right,type,true,null);
                }
            }
            if(_reservedAccess!=null)_reservedAccess.Visible=visible;
        }

        /// <summary>Design node id -> index of the lift whose platform it belongs to (moves with the cab).</summary>
        private readonly Dictionary<int,int> _liftNodeOf=new();
        private readonly List<float> _liftDy=new();
        private readonly Dictionary<int,float> _liftRatio=new();
        private Vector3 LiftOffset(int nodeId)=>_liftNodeOf.TryGetValue(nodeId,out int k)&&k<_liftDy.Count?new Vector3(0,_liftDy[k]*_liftRatio.GetValueOrDefault(nodeId,1),0):Vector3.Zero;

        private void AddTunnelAccess(TunnelGeometry geometry,BridgeDesign design)
        {
            _liftPlatforms.Clear();_liftNodeOf.Clear();_liftDy.Clear();_liftRatio.Clear();var plan=new TunnelAccessPlan(Level,geometry);
            var root=new Node3D {Name="TunnelLifts"};_container.AddChild(root);
            var layouts=_mm.Db==null?Array.Empty<Physics.LiftLayout>():Physics.LiftStructure.Analyze(design,Level,_mm.Db);
            var shafts=new[]{plan.Left,plan.Right};
            for(int k=0;k<2;k++)
            {
                var layout=layouts.ElementAtOrDefault(k);
                if(layout!=null)
                {
                    foreach(int id in layout.Platform)_liftNodeOf[id]=k;
                    if(Level.MechanicalLift)foreach(var motion in Physics.LiftMechanics.Plan(design,Level,_mm.Db,layout).Motion)
                    {_liftNodeOf[motion.Key]=k;_liftRatio[motion.Key]=(float)motion.Value;}
                }
                AddLift(root,shafts[k],geometry.Type,false,_liftPlatforms,layout,design);
                _liftDy.Add(0);
            }
            SetLiftPositions(Array.Empty<double>(),buildPose:true);
        }

        public void SetLiftPositions(IReadOnlyList<double> dy,IReadOnlyList<double> doors=null,bool buildPose=false)
        {
            for(int i=0;i<_liftPlatforms.Count;i++)
            {
                var lift=_liftPlatforms[i];if(!GodotObject.IsInstanceValid(lift.Platform))continue;
                float rest=lift.Down?lift.Top:lift.Bottom;
                // Construction uses the same lower-landing coordinates as snapping and member creation.
                float y=buildPose?lift.Bottom:Mathf.Clamp(rest+(i<dy.Count?(float)dy[i]:0),lift.Bottom,lift.Top);
                lift.Platform.Position=new Vector3(0,y,0);
                // The platform members are analysed at the lower landing: shift them by the cab's height above it
                if(i<_liftDy.Count)_liftDy[i]=y-lift.Bottom;
                if(lift.Structural)
                {
                    float rise=y-lift.Bottom;
                    foreach(var (weight,sheave,hang) in lift.Weights)
                    {
                        weight.Position=new Vector3(sheave.X,sheave.Y-hang-rise,sheave.Z);
                        if(lift.WeightRopes.TryGetValue(weight,out var r))SetCable(r,weight.Position,sheave,0.05f);
                    }
                }
                else{var counter=lift.Counterweight.Position;counter.Y=lift.Bottom+lift.Top-y+lift.CounterOffset;lift.Counterweight.Position=counter;}
                float open=doors!=null&&i<doors.Count?Mathf.Clamp((float)doors[i],0,1):1;
                float entry=Mathf.Abs(y-rest)<0.001f?open:0;
                float exit=Mathf.Abs(y-(lift.Down?lift.Bottom:lift.Top))<0.001f?open:0;
                lift.CageEntry.SetOpen(entry);lift.LandingEntry.SetOpen(entry);
                lift.CageExit.SetOpen(exit);lift.LandingExit.SetOpen(exit);
                if(lift.Structural)continue;
                foreach(var rope in lift.Ropes)SetCable(rope.mesh,lift.Platform.Position+rope.attachment,rope.top,0.045f);
                SetCable(lift.CounterRope,lift.Counterweight.Position+Vector3.Up*(lift.CounterOffset-0.4f),lift.CounterRopeTop,0.055f);
            }
        }

        private void AddLift(Node3D parent,TunnelRules.LiftShaft shaft,TunnelTypeConfig type,bool reserved,List<LiftVisual> visuals,
            Physics.LiftLayout layout=null,BridgeDesign design=null)
        {
            if(!reserved&&design!=null){AddStructuralLift(parent,shaft,type,visuals,layout,design);return;}
            var root=new Node3D {Name=shaft.Left?"LeftLift":"RightLift"};parent.AddChild(root);
            if(!shaft.Left)root.AddToGroup(World.TerrainBuilder.QuakeRightGroup);   // the right shaft moves with its bank in an earthquake
            var steel=reserved?_mm.PreviewOk:_mm.ForMaterial("steel");
            var deck=reserved?_mm.PreviewOk:_mm.ForMaterial("deck_steel");
            var concrete=reserved?_mm.PreviewOk:_mm.ForMaterial("concrete");
            var cable=TerrainBuilder.Mat(new Color("555e69"));var accent=TerrainBuilder.Mat(new Color(shaft.Left?"4ebfa9":"d6a458"));
            var glass=new StandardMaterial3D {AlbedoColor=new Color(0.36f,0.6f,0.69f,0.2f),Transparency=BaseMaterial3D.TransparencyEnum.Alpha,Roughness=0.3f};
            float x0=(float)shaft.X0,x1=(float)shaft.X1,cx=(x0+x1)/2,length=x1-x0;
            float bottom=(float)shaft.Bottom,top=(float)shaft.Top,w=RoadLayout.TubeEnvelope(type.TubeWidth),head=(float)(type.TubeHeight-type.FloorHeight);
            float frameTop=top+head,platformWidth=RoadLayout.Width+.8f,doorHeight=Mathf.Min(4.7f,head-0.45f),clear=RoadLayout.Width;
            var visual=new LiftVisual {Bottom=bottom,Top=top,Down=shaft.Left};
            foreach(float x in new[]{x0,x1})foreach(float z in new[]{-w/2,w/2})
                TubeBox(root,"LiftColumn",new Vector3(x,(bottom+frameTop)/2,z),new Vector3(0.35f,frameTop-bottom,0.35f),steel);
            for(float y=bottom+5;y<frameTop;y+=5)
            {
                foreach(float z in new[]{-w/2,w/2})TubeBox(root,"LiftBeam",new Vector3(cx,y,z),new Vector3(length,0.2f,0.2f),steel);
                var brace=TubeBox(root,"RearBrace",Vector3.Zero,Vector3.One,steel);
                SetCable(brace,new Vector3(x0+0.2f,y-4.8f,-w/2),new Vector3(x1-0.2f,y,-w/2),0.09f);
            }
            TubeBox(root,"LiftPit",new Vector3(cx,bottom-0.75f,0),new Vector3(length+0.5f,0.3f,w+0.4f),concrete);
            TubeBox(root,"LiftHead",new Vector3(cx,frameTop+0.2f,0),new Vector3(length+0.6f,0.35f,w+0.6f),concrete);
            foreach(float z in new[]{-platformWidth/2-0.15f,platformWidth/2+0.15f})
                foreach(float x in new[]{x0+0.35f,x1-0.35f})TubeBox(root,"GuideRail",new Vector3(x,(bottom+frameTop)/2,z),new Vector3(0.09f,frameTop-bottom,0.09f),cable);
            var platform=new Node3D {Name="LiftPlatform",Position=new Vector3(0,shaft.Left?top:bottom,0)};root.AddChild(platform);visual.Platform=platform;
            TubeBox(platform,"LiftDeck",new Vector3(cx,-0.15f,0),new Vector3(length-0.2f,0.3f,platformWidth),deck);
            foreach(float z in new[]{-platformWidth/2,platformWidth/2})
            {
                TubeBox(platform,"GuardPanel",new Vector3(cx,0.23f,z),new Vector3(length-0.6f,0.3f,0.08f),steel);
                TubeBox(platform,"Handrail",new Vector3(cx,1.05f,z),new Vector3(length-0.3f,0.08f,0.08f),accent);
                for(float x=x0+2;x<x1-1;x+=3)TubeBox(platform,"RailPost",new Vector3(x,0.7f,z),new Vector3(0.06f,0.7f,0.06f),steel);
                TubeBox(platform,"RoofRail",new Vector3(cx,doorHeight+0.35f,z),new Vector3(length-0.2f,0.12f,0.12f),steel);
                foreach(float x in new[]{x0+0.3f,x1-0.3f})TubeBox(platform,"CagePost",new Vector3(x,(doorHeight+0.35f)/2,z),new Vector3(0.13f,doorHeight+0.35f,0.13f),steel);
            }
            foreach(float x in new[]{x0+0.4f,x1-0.4f})TubeBox(platform,"WarningSill",new Vector3(x,0.015f,0),new Vector3(0.14f,0.03f,platformWidth),accent);
            visual.CageEntry=AddGate(platform,"CageEntryDoor",x0+0.12f,0,clear,platformWidth/2,doorHeight,steel,glass,accent);
            visual.CageExit=AddGate(platform,"CageExitDoor",x1-0.12f,0,clear,platformWidth/2,doorHeight,steel,glass,accent);
            visual.LandingEntry=AddGate(root,"BoardingLandingDoor",x0-0.12f,shaft.Left?top:bottom,clear,platformWidth/2,doorHeight,steel,glass,accent);
            visual.LandingExit=AddGate(root,"ExitLandingDoor",x1+0.12f,shaft.Left?bottom:top,clear,platformWidth/2,doorHeight,steel,glass,accent);
            TubeBox(root,"BoardingSill",new Vector3(x0-0.19f,(shaft.Left?top:bottom)-0.06f,0),new Vector3(0.5f,0.12f,platformWidth),deck);
            TubeBox(root,"ExitSill",new Vector3(x1+0.19f,(shaft.Left?bottom:top)-0.06f,0),new Vector3(0.5f,0.12f,platformWidth),deck);
            float weightHeight=Mathf.Min(2.4f,Mathf.Max(1.2f,(top-bottom)*0.3f)),weightWidth=Mathf.Clamp(length*0.3f,2.2f,5.5f);
            var weight=new Node3D {Name="LiftCounterweight",Position=new Vector3(cx,bottom+top-platform.Position.Y+weightHeight/2+0.4f,-w/2+0.5f)};
            root.AddChild(weight);visual.Counterweight=weight;visual.CounterOffset=weightHeight/2+0.4f;
            for(int i=0;i<6;i++)TubeBox(weight,"WeightPlate",new Vector3(0,-weightHeight/2+(i+0.5f)*weightHeight/6,0),new Vector3(weightWidth,weightHeight/6-0.025f,0.8f),cable);
            foreach(float x in new[]{cx-weightWidth/2-0.12f,cx+weightWidth/2+0.12f})
                TubeBox(root,"CounterweightGuide",new Vector3(x,(bottom+frameTop)/2,-w/2+0.5f),new Vector3(0.08f,frameTop-bottom,0.08f),steel);
            foreach(float x in new[]{x0+0.65f,x1-0.65f})foreach(float z in new[]{-platformWidth/2+0.18f,platformWidth/2-0.18f})
            {
                var topPoint=new Vector3(x,frameTop+0.75f,z);
                var rope=TubeBox(root,"HoistRope",Vector3.Zero,Vector3.One,cable);
                visual.Ropes.Add((rope,topPoint,new Vector3(x,doorHeight+0.45f,z)));
                TubeBox(platform,"RopeAnchor",new Vector3(x,doorHeight+0.4f,z),new Vector3(0.16f,0.2f,0.16f),steel);
                AddPulley(root,topPoint+Vector3.Back*0.26f,steel);
            }
            visual.CounterRope=TubeBox(root,"CounterweightRope",Vector3.Zero,Vector3.One,cable);
            visual.CounterRopeTop=new Vector3(cx,frameTop+0.75f,-w/2+0.5f);AddPulley(root,visual.CounterRopeTop+Vector3.Back*0.26f,steel);
            TubeBox(root,"HoistMotor",new Vector3(cx,frameTop+0.55f,0),new Vector3(0.9f,0.6f,0.8f),accent);
            AddEnclosure(root,platform,reserved,x0,x1,bottom,frameTop,w,platformWidth,doorHeight,clear,
                shaft.Left?top:bottom,shaft.Left?bottom:top,concrete,steel);
            if(visuals!=null)visuals.Add(visual);
            else
            {
                visual.CageEntry.SetOpen(1);visual.CageExit.SetOpen(0);visual.LandingEntry.SetOpen(1);visual.LandingExit.SetOpen(0);
                foreach(var rope in visual.Ropes)SetCable(rope.mesh,platform.Position+rope.attachment,rope.top,0.045f);
                SetCable(visual.CounterRope,weight.Position+Vector3.Up*weightHeight/2,visual.CounterRopeTop,0.055f);
            }
        }

        /// <summary>
        /// A built lift: the platform, hoists and shaft columns are the player's members (drawn with the structure).
        /// This adds only what is not a member: the shaft enclosure and landing doors, the cab dressing that rides on
        /// the platform (doors, side walls, handrails) and a counterweight hanging from every sheave, sized by its
        /// tonnage. The counterweight is at its highest while the cab is at the lower landing and drops as the cab rises.
        /// </summary>
        private void AddStructuralLift(Node3D parent,TunnelRules.LiftShaft shaft,TunnelTypeConfig type,List<LiftVisual> visuals,Physics.LiftLayout layout,BridgeDesign design)
        {
            var root=new Node3D {Name=shaft.Left?"LeftLift":"RightLift"};parent.AddChild(root);
            if(!shaft.Left)root.AddToGroup(World.TerrainBuilder.QuakeRightGroup);   // the right shaft moves with its bank in an earthquake
            var steel=_mm.ForMaterial("steel");var concrete=_mm.ForMaterial("concrete");
            var cable=TerrainBuilder.Mat(new Color("555e69"));var accent=TerrainBuilder.Mat(new Color(shaft.Left?"4ebfa9":"d6a458"));
            var glass=new StandardMaterial3D {AlbedoColor=new Color(0.36f,0.6f,0.69f,0.2f),Transparency=BaseMaterial3D.TransparencyEnum.Alpha,Roughness=0.3f};
            var c=Physics.LiftStructure.Cfg(_mm.Db);
            float x0=(float)shaft.X0,x1=(float)shaft.X1,cx=(x0+x1)/2,length=x1-x0;
            float bottom=(float)shaft.Bottom,top=(float)shaft.Top,w=RoadLayout.TubeEnvelope(type.TubeWidth),head=(float)(type.TubeHeight-type.FloorHeight);
            float pit=(float)Physics.LiftStructure.PitY(c,shaft),headY=(float)Physics.LiftStructure.HeadY(c,shaft);
            float platformWidth=RoadLayout.Width+.8f,doorHeight=Mathf.Min(4.7f,head-0.45f),clear=RoadLayout.Width;
            var visual=new LiftVisual {Bottom=bottom,Top=top,Down=shaft.Left,Structural=true};
            var platform=new Node3D {Name="LiftCab",Position=new Vector3(0,shaft.Left?top:bottom,0)};root.AddChild(platform);visual.Platform=platform;
            foreach(float z in new[]{-platformWidth/2,platformWidth/2})
            {
                TubeBox(platform,"Handrail",new Vector3(cx,1.05f,z),new Vector3(length-0.3f,0.08f,0.08f),accent);
                for(float x=x0+2;x<x1-1;x+=3)TubeBox(platform,"RailPost",new Vector3(x,0.7f,z),new Vector3(0.06f,0.7f,0.06f),steel);
            }
            foreach(float x in new[]{x0+0.4f,x1-0.4f})TubeBox(platform,"WarningSill",new Vector3(x,0.015f,0),new Vector3(0.14f,0.03f,platformWidth),accent);
            visual.CageEntry=AddGate(platform,"CageEntryDoor",x0+0.12f,0,clear,platformWidth/2,doorHeight,steel,glass,accent);
            visual.CageExit=AddGate(platform,"CageExitDoor",x1-0.12f,0,clear,platformWidth/2,doorHeight,steel,glass,accent);
            visual.LandingEntry=AddGate(root,"BoardingLandingDoor",x0-0.12f,shaft.Left?top:bottom,clear,platformWidth/2,doorHeight,steel,glass,accent);
            visual.LandingExit=AddGate(root,"ExitLandingDoor",x1+0.12f,shaft.Left?bottom:top,clear,platformWidth/2,doorHeight,steel,glass,accent);
            TubeBox(root,"LiftPit",new Vector3(cx,pit-0.15f,0),new Vector3(length+1.4f,0.3f,w+0.4f),concrete);
            double tonnes=Level.MechanicalLift?0:design.Tunnel?.Lift?.Counterweight??0;
            if(layout!=null&&tonnes>0)
            {
                int ropes=Math.Max(1,layout.Sheaves.Values.Sum());
                foreach(var (id,n) in layout.Sheaves)
                {
                    var s=design.FindNode(id);if(s==null)continue;
                    float share=(float)(tonnes*n/ropes),hgt=Mathf.Clamp(share/6f,0.5f,3f);
                    var sheave=new Vector3((float)s.Position.X,(float)s.Position.Y,-w/2+0.5f);
                    var weight=new Node3D {Name="LiftCounterweight"};root.AddChild(weight);
                    TubeBox(weight,"WeightBlock",new Vector3(0,-hgt/2,0),new Vector3(0.9f,hgt,0.7f),cable);
                    AddPulley(root,sheave+Vector3.Back*0.26f,steel);
                    visual.WeightRopes[weight]=TubeBox(root,"CounterweightRope",Vector3.Zero,Vector3.One,cable);
                    visual.Weights.Add((weight,sheave,1.2f));
                }
            }
            AddEnclosure(root,platform,false,x0,x1,pit+0.6f,headY,w,platformWidth,doorHeight,clear,
                shaft.Left?top:bottom,shaft.Left?bottom:top,concrete,steel);
            visuals?.Add(visual);
        }

        /// <summary>
        /// Closes the shaft and the cab: continuous concrete shaft walls (side, end and existing pit / head slab)
        /// with openings only at the two landing doors, and steel cab side walls plus a roof.
        /// The front (+z) walls of shaft and cab join the cutaway toggle so the inside can still be inspected.
        /// </summary>
        private void AddEnclosure(Node3D root,Node3D platform,bool reserved,float x0,float x1,float bottom,float frameTop,
            float w,float platformWidth,float doorHeight,float clear,float entryY,float exitY,Material wallMat,Material cabMat)
        {
            const float t=(float)TunnelRules.LiftEndWallThickness;
            float length=x1-x0,cx=(x0+x1)/2,yLow=bottom-0.9f,yHigh=frameTop+0.05f,half=w/2+0.3f;
            void Front(MeshInstance3D m){if(!reserved)_tubeFrontDetails.Add(m);}
            // Side walls of the shaft (back and front)
            foreach(int side in new[]{-1,1})
            {
                var wall=TubeBox(root,side<0?"ShaftWallBack":"ShaftWallFront",new Vector3(cx,(yLow+yHigh)/2,side*half),new Vector3(length+0.9f+2*t,yHigh-yLow,t),wallMat);
                if(side>0)Front(wall);
            }
            // End walls: solid except one door-sized opening at the landing on that end
            float wallOffset=(float)TunnelRules.LiftEndWallOffset;
            foreach(var (x,landing) in new[]{(x0-wallOffset,entryY),(x1+wallOffset,exitY)})
            {
                float openTop=landing+doorHeight+0.3f,zOpen=clear/2+0.3f,zWall=half-t/2;
                if(landing-0.12f>yLow)TubeBox(root,"ShaftEndWall",new Vector3(x,(yLow+landing-0.12f)/2,0),new Vector3(t,landing-0.12f-yLow,2*zWall),wallMat);
                if(yHigh>openTop)TubeBox(root,"ShaftEndWall",new Vector3(x,(openTop+yHigh)/2,0),new Vector3(t,yHigh-openTop,2*zWall),wallMat);
                float span=openTop-(landing-0.12f),strip=zWall-zOpen;
                if(strip>0.01f)foreach(int side in new[]{-1,1})
                {
                    var jamb=TubeBox(root,"ShaftEndWall",new Vector3(x,landing-0.12f+span/2,side*(zOpen+strip/2)),new Vector3(t,span,strip),wallMat);
                    if(side>0)Front(jamb);
                }
            }
            if(Level.MechanicalLift)return;
            // Cab: closed side walls and a roof; doors at both ends already close the openings
            float cabH=doorHeight+0.35f;
            foreach(int side in new[]{-1,1})
            {
                var panel=TubeBox(platform,side<0?"CabWallBack":"CabWallFront",new Vector3(cx,cabH/2,side*(platformWidth/2+0.05f)),new Vector3(length-0.3f,cabH,0.06f),cabMat);
                if(side>0)Front(panel);
            }
            TubeBox(platform,"CabRoof",new Vector3(cx,cabH+0.03f,0),new Vector3(length-0.2f,0.06f,platformWidth+0.16f),cabMat);
        }

        private static SlidingGate AddGate(Node3D parent,string name,float x,float y,float clear,float halfSpan,float height,Material metal,Material glass,Material accent)
        {
            var gate=new Node3D {Name=name,Position=new Vector3(x,y,0)};parent.AddChild(gate);
            // Fixed fences close the side gaps between the door jambs and the cab / shaft sides
            float f0=clear/2+0.17f,f1=halfSpan;
            if(f1-f0>0.05f)foreach(int side in new[]{-1,1})
            {
                float zc=side*(f0+f1)/2,span=f1-f0;
                foreach(float yy in new[]{0.08f,height/2,height-0.06f})TubeBox(gate,"GapFenceRail",new Vector3(0,yy,zc),new Vector3(0.08f,0.08f,span),metal);
                for(float z=f0+0.12f;z<f1;z+=0.18f)TubeBox(gate,"GapFenceBar",new Vector3(0,height/2,side*z),new Vector3(0.04f,height,0.04f),metal);
                TubeBox(gate,"GapFenceStripe",new Vector3(-0.05f,0.94f,zc),new Vector3(0.02f,0.1f,span),accent);
            }
            TubeBox(gate,"DoorHeader",new Vector3(0,height+0.1f,0),new Vector3(0.18f,0.2f,clear+0.4f),metal);
            foreach(float z in new[]{-clear/2-0.1f,clear/2+0.1f})TubeBox(gate,"DoorJamb",new Vector3(0,height/2,z),new Vector3(0.16f,height,0.14f),metal);
            var result=new SlidingGate {Travel=clear/2+0.04f};
            for(int side=-1;side<=1;side+=2)
            {
                var leaf=new Node3D {Name=side<0?"LeftLeaf":"RightLeaf",Position=new Vector3(0,0,side*clear/4)};gate.AddChild(leaf);
                float width=clear/2-0.04f;
                TubeBox(leaf,"DoorLowerPanel",new Vector3(0,0.5f,0),new Vector3(0.1f,1,width),metal);
                TubeBox(leaf,"SafetyStripe",new Vector3(-0.058f,0.94f,0),new Vector3(0.018f,0.1f,width),accent);
                TubeBox(leaf,"DoorWindow",new Vector3(0,(height+1)/2,0),new Vector3(0.035f,height-1.1f,width-0.1f),glass);
                foreach(float z in new[]{-width/2,0,width/2})TubeBox(leaf,"WindowGuard",new Vector3(0,(height+1)/2,z),new Vector3(0.065f,height-0.9f,0.045f),metal);
                TubeBox(leaf,"TopRail",new Vector3(0,height-0.04f,0),new Vector3(0.1f,0.08f,width),metal);
                if(side<0){result.Left=leaf;result.LeftClosed=leaf.Position;}else{result.Right=leaf;result.RightClosed=leaf.Position;}
            }
            return result;
        }

        private static void AddPulley(Node3D parent,Vector3 position,Material material)
        {
            var pulley=new MeshInstance3D {Name="HoistPulley",Mesh=new CylinderMesh {TopRadius=0.26f,BottomRadius=0.26f,Height=0.14f,RadialSegments=12,Rings=1},MaterialOverride=material,Position=position,Rotation=new Vector3(0,0,Mathf.Pi/2)};
            parent.AddChild(pulley);
            TubeBox(parent,"PulleyBracket",position-Vector3.Up*0.34f,new Vector3(0.18f,0.18f,0.44f),material);
        }
    }
}
