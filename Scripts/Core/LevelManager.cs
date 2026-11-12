using System.Linq;
using BridgeArchitect3D.Building;
using BridgeArchitect3D.CameraControl;
using BridgeArchitect3D.Data;
using BridgeArchitect3D.Physics;
using BridgeArchitect3D.Traffic;
using BridgeArchitect3D.UI;
using BridgeArchitect3D.World;
using Godot;

namespace BridgeArchitect3D.Core
{
    /// <summary>
    /// 关卡场景根节点：组装地形、相机、建造系统、通车测试与 HUD，并管理建造 / 测试两种模式。
    /// 建造状态（DesignEditor）与测试状态（BridgeSimulation 副本）完全独立。
    /// </summary>
    public partial class LevelManager : Node3D, ILevelController
    {
        private GameManager _gm;
        private GameDatabase _db;
        private LevelDefinition _level;
        private DesignEditor _editor;
        private OrbitCamera _camera;
        private BridgeRenderer _renderer;
        private BuildingManager _building;
        private TrafficManager _traffic;
        private GameHUD _hud;
        private bool _testing;
        private double _propTimer;
        private Node3D _decorations, _overlays;

        public override void _Ready()
        {
            _gm = GameManager.Instance;
            if (_gm?.Db == null) { ShowFatal(_gm?.LoadError ?? Loc.T("游戏数据未加载")); return; }
            _db = _gm.Db;
            _level = _gm.ResolveLevel(_gm.CurrentLevelId) ?? _db.Levels[0];

            _decorations = TerrainBuilder.Build(this, _level);
            foreach (var a in _level.Anchors.Where(a => a.Enabled))
            {
                AddChild(new Label3D
                {
                    Text = a.Id + (a.IsRoadStart ? Loc.T(" 起点") : a.IsRoadEnd ? Loc.T(" 终点") : ""),
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    FontSize = 40, PixelSize = 0.01f, Modulate = new Color(1f, 0.75f, 0.4f),
                    Font = UiTheme.Get().DefaultFont,
                    NoDepthTest = true,          // 标签总在最前，不会被岸边地形遮住
                    OutlineSize = 8, OutlineModulate = new Color(0, 0, 0, 0.8f),
                    Position = new Vector3((float)a.Position.X, (float)a.Position.Y + 0.9f, BuildPlane.FrontZ + 0.5f),
                });
            }

            _camera = new OrbitCamera { Current = true, Fov = 55, Far = 600 };
            AddChild(_camera);
            // 默认正交侧视：两个路口与所有锚点都落在画面内（考虑左侧工具栏与底栏的遮挡）
            float w = (float)_level.Terrain.RiverWidth;
            float minY = (float)_level.Anchors.Min(a => a.Position.Y) - 1.5f;
            float maxY = Mathf.Max(6, (float)_level.Anchors.Max(a => a.Position.Y) + 6);
            float aspect = Mathf.Max(1.2f, GetViewport().GetVisibleRect().Size.Aspect());
            float needH = Mathf.Max(maxY - minY, (w + 16) / aspect) * 1.25f;
            _camera.Setup(new Vector3(w / 2 - 1.2f, (minY + maxY) / 2 - 0.8f, 0), needH / 0.85f);
            _camera.SideView(true);
            _camera.Sensitivity = (float)_gm.Settings.CameraSensitivity;

            var mm = new MaterialManager(_db);
            _renderer = new BridgeRenderer { Name = "Bridge", Level = _level };
            AddChild(_renderer);
            _renderer.Init(mm);

            BridgeDesign design = null;
            if (_gm.LoadSavedDesign)
            {
                design = SaveManager.LoadDesign(_level.Id, out var err);
                if (design == null && err != null) GD.PushWarning(err);
            }
            // 关卡可以提供一座“半成品”起始桥（例如第一关缺一根斜撑），没有存档时载入
            if (design == null && !string.IsNullOrEmpty(_level.StartDesign)) design = SaveManager.LoadStartDesign(_level.StartDesign);
            _editor = new DesignEditor(design, _level, _db);

            var canvas = new CanvasLayer();
            AddChild(canvas);
            _hud = new GameHUD();
            canvas.AddChild(_hud);
            var levelVehicles = _level.TrafficEvents.Where(e => e.VehicleId != null).Select(e => e.VehicleId).Distinct()
                .Select(id => _db.Vehicles.TryGetValue(id, out var v) ? v : null).Where(v => v != null).ToList();
            _hud.Init(this, _level.Name, GoalText(), _level.AllowedMaterials.Select(id => _db.GetMaterial(id)).Where(x => x != null).ToList(),
                _db.Materials.Values.ToList(), levelVehicles);

            _building = new BuildingManager { Name = "Building" };
            AddChild(_building);
            _building.Message += (t, e) => _hud.ShowToast(t, e);
            _building.Preview += (t, p, ok) => _hud.ShowPreview(t, p, ok);
            _building.ToolChanged += () => _hud.SetTool(_building.Tool, _building.ToolMaterial);
            _building.SelectionChanged += RefreshProperties;
            _building.BoxChanged += r => _hud.ShowBoxRect(r);
            _editor.Changed += RefreshBudget;
            _building.Init(_editor, _camera, _renderer, mm, _gm.Settings.ShowGrid);

            _traffic = new TrafficManager { Name = "Traffic" };
            AddChild(_traffic);
            _traffic.Init(_renderer);
            _traffic.Prewarm(levelVehicles);
            _traffic.DeformationScale = (float)_gm.Settings.DeformationScale;
            _traffic.Finished += OnTestFinished;
            _traffic.MemberBrokenVisual += e => _hud.ShowToast(e.Text, true);
            _traffic.MemberCrackedVisual += e => _hud.ShowToast("⚠ " + e.Text + Loc.T("：接近失效，注意加强"), false, 3);

            _hud.SetTool(BuildTool.Select, null);
            RefreshBudget();
            RefreshProperties();
            _hud.SetStructureStatus(StaticStatusText(out var c), c);
            ShowFirstStep();
        }

        private MeshInstance3D _water;
        private float _waterBaseY, _waterBaseH;

        /// <summary>洪水时抬高水面（纯显示，冲刷与冲毁由模拟判定）。</summary>
        private void UpdateFloodWater()
        {
            if (_water == null)
            {
                _water = FindChild("Water", true, false) as MeshInstance3D;
                if (_water == null) return;
                _waterBaseY = _water.Position.Y; _waterBaseH = (float)(_level.Terrain.WaterLevel - _level.Terrain.RiverBedDepth);
            }
            float rise = _testing && _traffic?.Sim != null ? (float)_traffic.Sim.FloodRise : 0;
            float h = _waterBaseH + rise;
            _water.Scale = new Vector3(1, h / _waterBaseH, 1);
            _water.Position = new Vector3(_water.Position.X, _waterBaseY - _waterBaseH / 2 + h / 2, _water.Position.Z);
        }

        /// <summary>关卡目标文字（顶栏显示）。</summary>
        private string GoalText()
        {
            var parts = _level.TrafficEvents.Where(e => e.Type == TrafficEventType.Vehicle)
                .GroupBy(e => e.VehicleId).Select(g => Loc.F("{0} 辆{1}", (g.Count()), (_db.Vehicles[g.Key].Name))).ToList();
            var ships = _level.TrafficEvents.Count(e => e.Type == TrafficEventType.Ship);
            string s = Loc.T("目标：让 ") + string.Join(Loc.T("、"), parts) + Loc.T(" 通过");
            if (ships > 0) s += Loc.F("，并给 {0} 艘船留出航道", (ships));
            if (_level.TrafficEvents.Any(e => e.Type == TrafficEventType.Environment)) s += Loc.T("，并扛住一场洪水");
            return s;
        }

        /// <summary>第一步引导：空桥时选中桥面工具并高亮左岸起点；已有起始桥时提示先试车。</summary>
        private void ShowFirstStep()
        {
            var startAnchor = _editor.Design.Nodes.FirstOrDefault(n => n.IsAnchor && _level.Anchors.Any(a => a.Id == n.AnchorId && a.IsRoadStart));
            if (_editor.Design.Members.Count == 0)
            {
                var deck = _building.MaterialOrder.FirstOrDefault(id => id == "road") ?? _building.MaterialOrder.FirstOrDefault(id => _db.GetMaterial(id).IsDeck);
                if (deck != null) _building.SetTool(BuildTool.Build, deck);
                if (startAnchor != null) _building.SetHintNode(startAnchor.Id);
                _hud.ShowToast(Loc.T("点击发光锚点，铺第一段桥面"), false, 6);
            }
            else if (!string.IsNullOrEmpty(_level.StartHint)) _hud.ShowToast(_level.StartHint, false, 7);
            else _hud.ShowToast(_level.Description, false);
        }

        public void SetView(bool side)
        {
            if (side) _camera.SideView(); else _camera.PerspectiveView();
            _hud.SetViewButtons(side);
        }

        private void ShowFatal(string msg)
        {
            var canvas = new CanvasLayer(); AddChild(canvas);
            var vb = new VBoxContainer { Theme = UiTheme.Get() };
            vb.SetAnchorsPreset(Control.LayoutPreset.Center);
            vb.AddChild(UiTheme.MakeLabel(msg, 18, UiTheme.Bad));
            vb.AddChild(UiTheme.MakeButton(Loc.T("返回主菜单"), () => GetTree().ChangeSceneToFile(GameManager.MainMenuScene)));
            canvas.AddChild(vb);
        }

        public override void _Process(double delta)
        {
            // 建造时的正交侧视隐藏远景装饰，减少干扰；3D 视角与测试时恢复
            if (_decorations != null && _camera != null) _decorations.Visible = _testing || !_camera.IsOrtho;
            _overlays ??= FindChild(TerrainBuilder.OverlayName, true, false) as Node3D;
            if (_overlays != null) _overlays.Visible = !_testing;
            UpdateFloodWater();
            if (_hud == null || !_testing || _traffic.Sim == null) return;
            var sim = _traffic.Sim;
            // The status line is rebuilt 20 times a second (string building + label relayout every frame was waste)
            _hudTimer -= delta;
            if (_hudTimer <= 0) { _hudTimer = 0.05; UpdateTestHud(sim); }

            _propTimer -= delta;
            if (_propTimer <= 0 && _traffic.SelectedMember >= 0)
            {
                _propTimer = 0.1;
                var m = _traffic.Sim.Design.FindMember(_traffic.SelectedMember);
                if (m != null) _hud.ShowProperties(GameHUD.MemberProperties(m, _db, sim.Model, sim.Status, sim.Output.Stable || sim.State != SimState.Failed, Loc.F("测试工况 t={0:0.0}s", (sim.Time))));
            }
        }

        private double _hudTimer;

        private void UpdateTestHud(BridgeSimulation sim)
        {
            string vehicles = string.Join(Loc.T("、"), sim.Vehicles.Where(v => !v.Finished).Select(v => v.Def.Name));
            if (string.IsNullOrEmpty(vehicles)) vehicles = "—";
            vehicles += Loc.F("（已通过 {0}/{1}）", (sim.FinishedVehicles), (sim.TotalVehicles));
            if (sim.Vehicles.Any(v => v.WaitingForBridge && !v.Finished)) vehicles += Loc.T(" · 开启桥动作中，车辆在活动桥面前停车等候");
            if (sim.Ships.Count > 0)
            {
                var sailing = sim.Ships.Where(s => s.Spawned && !s.Done).Select(s => s.Ev.Name).ToList();
                vehicles += Loc.F("  船只 {0}/{1}", (sim.Ships.Count(s => s.Done)), (sim.Ships.Count)) + (sailing.Count > 0 ? Loc.F("（{0}航行中）", (string.Join(Loc.T("、"), sailing))) : "");
                if (sim.Ships.Any(s => s.Waiting)) vehicles += Loc.T(" · 船只等候开桥");
                if (sim.HydraulicFactor > 0) vehicles += Loc.F("  液压杆收缩 {0:0}%", (sim.HydraulicFactor * 100));
            }
            // Movable bridge state and faults (stroke / force / alignment / configuration)
            if (sim.Bascule.Leaves.Count > 0) vehicles += "  " + sim.Bascule.StatusText;
            if (sim.Bascule.Fault != null) vehicles += "  ⚠ " + sim.Bascule.Fault;
            if (sim.Bascule.ConfigNote != null) vehicles += "  ⚠ " + sim.Bascule.ConfigNote;
            if (sim.FloodRise > 0.01) vehicles += Loc.F("  🌊 洪水 +{0:0.0} m", (sim.FloodRise));
            string status; Color col;
            if (_traffic.IsReplaying) { status = Loc.F("回放中 t={0:0.0}s（按“继续”退出回放）", (_traffic.ReplayFrameTime)); col = UiTheme.Warn; }
            else if (sim.State == SimState.Failed) { status = Loc.T("失败：") + FailureDetector.ReasonText(sim.Result.FailureReason); col = UiTheme.Bad; }
            else if (sim.State == SimState.Succeeded) { status = Loc.T("全部车辆通过"); col = UiTheme.Good; }
            else
            {
                double u = sim.CurrentMaxUtilization;
                status = Loc.F("{0}  当前最大利用率 {1:0}%  断裂 {2}", ((u > 0.9 ? Loc.T("接近极限") : Loc.T("安全"))), (u * 100), (sim.BrokenCount));
                col = u > 0.9 ? UiTheme.Warn : UiTheme.Good;
            }
            _hud.SetLoad(sim.CurrentMaxUtilization);
            _hud.UpdateTest(sim.Progress, vehicles, status, col, _traffic.Paused || _traffic.IsReplaying, _traffic.IsReplaying ? _traffic.ReplayFrameTime : sim.Time, _traffic.TimelineEnd, sim.State is SimState.Succeeded or SimState.Failed);
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (_hud == null) return;
            if (e is InputEventKey k && k.Pressed && !k.Echo)
            {
                if (k.Keycode == Key.Space) { if (_testing) TogglePause(); else StartTest(); }
                else if (k.Keycode == Key.Tab) { bool on = _testing ? !_traffic.ShowStress : !_building.StressView; SetStressView(on); _hud.SetStressChecked(on); }
                else if (k.Keycode == Key.Escape && _testing) BackToBuild();
            }
            // 测试模式下点击构件查看实时内力
            if (_testing && e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                var from = _camera.ProjectRayOrigin(mb.Position);
                var hit = new Plane(new Vector3(0, 0, 1), BuildPlane.FrontZ).IntersectsRay(from, _camera.ProjectRayNormal(mb.Position));
                if (hit != null)
                {
                    var m = _editor.MemberAt(hit.Value.X, hit.Value.Y, 0.4);
                    _traffic.SelectedMember = m?.Id ?? -1;
                    if (m == null) _hud.ShowProperties(null);
                    _propTimer = 0;
                    _traffic.UpdateVisuals();
                }
            }
        }

        // ---------------- 数据刷新 ----------------
        private void RefreshBudget()
        {
            foreach (var id in _level.MaterialLimits.Keys) _hud.SetToolBadge(id, _editor.RemainingOf(id));
            _hud.SetDemolitionInfo(_level.MaxDemolitions >= 0 ? Loc.F("拆除旧构件 {0}/{1}", (_editor.Design.DemolishedCount), (_level.MaxDemolitions)) : null);
            _hud.UpdateBudget(_editor.UsedCost, _editor.Budget, _editor.CanUndo && !_testing, _editor.CanRedo && !_testing);
            if (!_testing) _hud.SetStructureStatus(StaticStatusText(out var c), c);
        }

        private string StaticStatusText(out Color c)
        {
            var s = _building?.StaticSim;
            if (s == null || s.Model == null || s.Model.MemberCount == 0) { c = UiTheme.Muted; _hud.SetLoad(0, false); return Loc.T("尚未建造构件"); }
            if (!s.Output.Stable) { c = UiTheme.Bad; _hud.SetLoad(1); return Loc.T("自重下不稳定：") + s.Output.Message; }
            double u = s.CurrentMaxUtilization;
            _hud.SetLoad(u);
            c = u > 1 ? UiTheme.Bad : u > 0.8 ? UiTheme.Warn : UiTheme.Good;
            return Loc.F("自重下稳定，最大利用率 {0:0}%", (u * 100));
        }

        private void RefreshProperties()
        {
            if (_testing) return;
            var d = _editor.Design;
            var stat = _building.StaticSim;
            if (_building.SelectedNodeId >= 0 && d.FindNode(_building.SelectedNodeId) is { } n)
                _hud.ShowProperties(GameHUD.NodeProperties(n, _level, d, stat));
            else if (_building.SelectedMemberId >= 0 && d.FindMember(_building.SelectedMemberId) is { } m)
                _hud.ShowProperties(GameHUD.MemberProperties(m, _db, stat?.Model, stat?.Status, stat?.Output.Stable ?? false, Loc.T("自重工况（无车辆）")));
            else _hud.ShowProperties(null);
            var selMat = _building.SelectedMemberId >= 0 ? _db.GetMaterial(d.FindMember(_building.SelectedMemberId)?.MaterialId) : null;
            var selMem = _building.SelectedMemberId >= 0 ? d.FindMember(_building.SelectedMemberId) : null;
            bool canRepair = !_testing && selMem != null && selMem.Existing && selMem.Condition < 1 - 1e-9;
            _hud.SetRepair(canRepair, canRepair ? _editor.RepairCostOf(selMem) : 0);
            var selNode = _building.SelectedNodeId >= 0 ? d.FindNode(_building.SelectedNodeId) : null;
            // Foundation buttons for any pier node or pier member (the base node sits on the riverbed, hard to pick)
            BridgeNode pierBase = selNode != null ? _editor.PierBaseOf(selNode.Id)
                : selMem != null && selMat?.IsPier == true ? (_editor.PierBaseOf(selMem.StartNodeId) ?? _editor.PierBaseOf(selMem.EndNodeId)) : null;
            _pierBaseId = pierBase?.Id ?? -1;
            _hud.SetFoundationRow(!_testing && pierBase != null, pierBase?.Foundation ?? FoundationType.None);
            var mech = d.EnsureMechanisms();
            bool isLock = selNode != null && mech.LockOf(selNode.Id) != null;
            bool lockable = !_testing && selNode != null && !selNode.IsAnchor && !selNode.PierBase && !selNode.PierPart;
            bool hyd = !_testing && selMat != null && selMat.Extendable;
            bool isLeaf = selMem != null && mech.Leaves.Any(l => l.ActuatorMemberId == selMem.Id);
            bool split = !_testing && selMem != null && selMat != null && !selMat.Extendable && !selMat.IsPier && !selMem.Existing;
            var driver=mech.Linkage?.Drivers.FirstOrDefault(v=>v.MemberId==selMem?.Id);
            _hud.SetMechanismRow(lockable, !_testing && isLock, hyd, isLeaf, split, (mech.Linkage?.Drivers.Count ?? 0)>0, driver!=null, driver?.Ratio ?? 0, !_testing && selNode?.IsRigidSplit==true);
            if (hyd) { var hr = _editor.GetHydraulicRange(selMem.Id); _hud.SetHydraulicRange(hr.Valid, hr.BuiltLength, hr.MinPct, hr.MaxPct, hr.CurrentPct, hr.Leaf); }
            else _hud.SetHydraulicRange(false, 1, 0, 0, 0, false);
            RefreshBudget();
        }

        // ---------------- ILevelController ----------------
        public void Undo() { if (!_testing) _editor.Undo(); }
        public void Redo() { if (!_testing) _editor.Redo(); }

        public void ResetLevel()
        {
            if (_testing) return;
            // 加固关重置回原来的旧桥；其他关卡重置为空桥
            var start = string.IsNullOrEmpty(_level.StartDesign) ? null : SaveManager.LoadStartDesign(_level.StartDesign);
            _editor.ReplaceDesign(start != null && start.Members.Any(m => m.Existing) ? start : BridgeDesign.CreateForLevel(_level));
            _hud.ShowToast(Loc.T("关卡已重置（可撤销）"), false);
        }

        public void SaveDesign()
        {
            if (SaveManager.SaveDesign(_editor.Design, out var err)) _hud.ShowToast(Loc.T("方案已保存"), false);
            else _hud.ShowToast(Loc.T("保存失败：") + err, true);
        }

        public void SaveDesignAs(string name)
        {
            if (SaveManager.SaveNamedDesign(_editor.Design, name, out var err)) _hud.ShowToast(Loc.F("方案“{0}”已保存", (name.Trim())), false);
            else _hud.ShowToast(Loc.T("保存失败：") + err, true);
        }

        public System.Collections.Generic.IReadOnlyList<string> NamedDesigns() => SaveManager.ListNamedDesigns(_level.Id);

        public void LoadNamedDesign(string name)
        {
            if (_testing) return;
            var d = SaveManager.LoadNamedDesign(_level.Id, name, out var err);
            if (d == null) { _hud.ShowToast(err, true); return; }
            _editor.ReplaceDesign(d);
            _hud.ShowToast(Loc.F("已加载方案“{0}”：{1} 根构件，已用 ${2:N0}（可撤销）", (name), (d.Members.Count), (_editor.UsedCost)), false);
        }

        public void LoadDesign()
        {
            if (_testing) return;
            var d = SaveManager.LoadDesign(_level.Id, out var err);
            if (d == null) { _hud.ShowToast(err, true); return; }
            _editor.ReplaceDesign(d);
            _hud.ShowToast(Loc.F("已加载存档：{0} 个节点，{1} 根构件，已用 ${2:N0}", (d.Nodes.Count), (d.Members.Count), (_editor.UsedCost)), false);
        }

        private int _referenceStep;

        /// <summary>参考方案分三步：① 提示思路 → ② 示意局部（左半座桥）→ ③ 载入完整方案。每一步都可撤销。</summary>
        public void LoadReference()
        {
            if (_testing) return;
            var d = SaveManager.LoadReference(_level.Id);
            if (_referenceStep == 0)
            {
                _referenceStep = 1;
                _hud.ShowToast((_level.ReferenceHint ?? Loc.T("试着用三角形支撑桥面节点。")) + Loc.T("　（再次点击“参考方案提示”可查看局部示意）"), false, 9);
                return;
            }
            if (d == null) { _hud.ShowToast(Loc.T("没有找到参考方案"), true); return; }
            if (_referenceStep == 1)
            {
                _referenceStep = 2;
                // 局部示意：只保留左半座桥（构件两端都在河宽一半以内），其余留给玩家完成
                double half = _level.Terrain.RiverWidth / 2 + 0.01;
                var part = d.Clone();
                part.Members.RemoveAll(m => !m.Existing && (part.FindNode(m.StartNodeId).Position.X > half || part.FindNode(m.EndNodeId).Position.X > half));   // 旧桥原有构件保留
                part.Nodes.RemoveAll(n => !n.IsAnchor && !part.Members.Any(m => m.StartNodeId == n.Id || m.EndNodeId == n.Id));
                _editor.ReplaceDesign(part);
                _hud.ShowToast(Loc.T("已载入左半座桥的示意（可撤销）。照着它完成右半边，或再次点击载入完整方案。"), false, 8);
                return;
            }
            _editor.ReplaceDesign(d);
            _hud.ShowToast(Loc.T("已载入完整参考方案（可撤销）"), false);
        }

        public void BackToMenu()
        {
            if (!_testing) SaveManager.SaveDesign(_editor.Design, out _);
            _gm.GoToMainMenu();
        }

        public void OpenLevelSelect()
        {
            _gm.ShowLevelSelectOnMenu = true;
            BackToMenu();
        }

        public void SelectTool(BuildTool tool, string materialId)
        {
            if (_testing) return;
            // The delete button with a multi-selection deletes it directly (same as the X key)
            if (tool == BuildTool.Delete && _building.MultiSelectionCount > 0) { _building.DeleteSelected(); _hud.SetTool(_building.Tool, _building.ToolMaterial); return; }
            _building.SetTool(tool, materialId);
        }

        // Scrubbing only after the test has ended; a running test is never frozen by the bar
        public void SeekTest(double time) { if (_testing && _traffic.Sim?.State is SimState.Succeeded or SimState.Failed) _traffic.Seek(time); }
        public void SetFoundationSelected(FoundationType type)
        {
            if (_testing || _pierBaseId < 0) return;
            var r = _editor.SetFoundation(_pierBaseId, type);
            _hud.ShowToast(r.Ok ? (r.Message ?? "") : r.Message, !r.Ok);
        }

        private bool _strokeDragUndo;
        private int _pierBaseId = -1;

        private BuildResult CommitStroke(int mem, double pct)
        {
            bool undo = !_strokeDragUndo; _strokeDragUndo = false;
            return _editor.SetHydraulicTarget(mem, pct, undo);
        }

        public void MechanismAction(string action)
        {
            if (_testing) return;
            if (action.StartsWith("preview:"))
            {
                // Slider drag: update the design (and the yellow sleeve) live; one undo step for the whole drag
                int pm = _building.SelectedMemberId;
                if (pm >= 0 && double.TryParse(action.Substring(8), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pv))
                {
                    _editor.SetHydraulicTarget(pm, pv / 100, !_strokeDragUndo);
                    _strokeDragUndo = true;
                }
                return;
            }
            int node = _building.SelectedNodeId, mem = _building.SelectedMemberId;
            var leaf = mem >= 0 ? _editor.Design.EnsureMechanisms().Leaves.FirstOrDefault(l => l.ActuatorMemberId == mem) : null;
            BuildResult r = action switch
            {
                "lock" when node >= 0 => _editor.CreateDeckLock(node),
                "unlock" when node >= 0 => _editor.RemoveDeckLock(node),
                _ when mem >= 0 && action.StartsWith("target:") && double.TryParse(action.Substring(7), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tp) => CommitStroke(mem, tp / 100),
                _ when mem >= 0 && action.StartsWith("stroke:") && double.TryParse(action.Substring(7), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct) => _editor.SetHydraulicStroke(mem, pct / 100),
                "retract" when mem >= 0 => _editor.SetHydraulicDirection(mem,false),
                "extend" when mem >= 0 => _editor.SetHydraulicDirection(mem,true),
                "describe" when mem >= 0 => BuildResult.Success(0, _editor.DescribeActuator(mem)),
                "leaf" when mem >= 0 => _editor.SetLeafActuator(mem),
                "unleaf" when mem >= 0 => _editor.RemoveLeaf(mem),
                "split" when mem >= 0 => _editor.SplitMember(mem,false),
                "rigidSplit" when mem >= 0 => _editor.SplitMember(mem,true),
                "convertSplit" when node >= 0 => _editor.ConvertRigidToSplit(node),
                _ => BuildResult.Fail(Loc.T("请先选中节点或构件")),
            };
            var targets=DiagnosticTargets.Parse(r.Message,mem);
            _building.ShowDiagnostic(targets);
            _hud.ShowToast(r.Message ?? "", !r.Ok, hasSceneTargets:_building.DiagnosticPositions.Any());
        }

        public void ClearDiagnostic() { _building.ClearDiagnostic(); }
        public void LocateDiagnostic()
        {
            if(_testing) { LocateProblem(); return; }
            var points=_building.DiagnosticPositions.ToList();
            if(points.Count==0) return;
            var lo=points.Aggregate((a,b)=>new Vector3(Mathf.Min(a.X,b.X),Mathf.Min(a.Y,b.Y),0));
            var hi=points.Aggregate((a,b)=>new Vector3(Mathf.Max(a.X,b.X),Mathf.Max(a.Y,b.Y),0));
            _camera.FrameDiagnostic((lo+hi)*0.5f,hi-lo);
            _hud.SetViewButtons(true);
        }

        public void RepairSelected()
        {
            if (_testing || _building.SelectedMemberId < 0) return;
            var r = _editor.RepairMember(_building.SelectedMemberId);
            _hud.ShowToast(r.Ok ? r.Message : r.Message, !r.Ok);
        }

        public void SetStressView(bool on)
        {
            if (_testing) { _traffic.ShowStress = on; _traffic.UpdateVisuals(); }
            else _building.SetStressView(on);
        }

        public void DeleteSelected() { if (!_testing) _building.DeleteSelected(); }

        /// <summary>Benchmark only: full reference design, 3D view, then run the traffic test.</summary>
        public double BenchSimTime => _traffic?.Sim?.Time ?? -1;
        public void BenchmarkStart()
        {
            var d = SaveManager.LoadReference(_level.Id);
            if (d != null) _editor.ReplaceDesign(d);
            SetView(false);
            StartTest();
        }

        public void StartTest()
        {
            if (_testing) return;
            SaveManager.SaveDesign(_editor.Design, out _);   // 自动保存当前方案
            _testing = true;
            _building.SetEnabled(false);
            _hud.SetMode(true);
            _hud.ShowProperties(null);
            _traffic.SelectedMember = -1;
            _traffic.ShowStress = true;
            _hud.SetStressChecked(true);
            _traffic.SpeedMultiplier = _traffic.SpeedMultiplier <= 0 ? 1 : _traffic.SpeedMultiplier;
            _traffic.Begin(_editor.Design, _level, _db);
            RefreshBudget();
        }

        public void TogglePause()
        {
            if (!_testing) return;
            if (_traffic.IsReplaying) { _traffic.StopReplay(); return; }
            _traffic.Paused = !_traffic.Paused;
        }

        public void ResetTest()
        {
            if (!_testing) { StartTest(); return; }
            _hud.HideDialogs();
            _traffic.Begin(_editor.Design, _level, _db);   // 从原始方案重新开始，恢复全部构件状态
            _hud.SetMode(true);
        }

        public void BackToBuild()
        {
            if (!_testing) { _hud.HideDialogs(); return; }
            _traffic.Clear();
            _testing = false;
            _hud.SetMode(false);
            _building.SetEnabled(true);
            _hud.SetStressChecked(_building.StressView);
            RefreshBudget();
            RefreshProperties();
        }

        /// <summary>失败后“定位问题并返回建造”：回到建造模式，切到侧视，高亮并选中出问题的构件。</summary>
        public void LocateProblem()
        {
            var r = _traffic.Sim?.Result;
            var ids = r?.InvolvedMemberIds.Distinct().ToList() ?? new System.Collections.Generic.List<int>();
            string headline = r?.Headline, tip = r?.TopSuggestion;
            BackToBuild();
            SetView(true);
            if (ids.Count > 0)
            {
                _building.HighlightProblem(ids);
                _hud.ShowToast($"{headline}　→ {tip}", true, 8);
            }
            else _hud.ShowToast(string.IsNullOrEmpty(tip) ? Loc.T("已返回建造") : tip, false, 6);
        }

        public void SetSpeed(int x) { _traffic.SpeedMultiplier = x; }

        public void NextLevel()
        {
            var next = _gm.NextLevelId(_level.Id);
            if (next == null) { _gm.ShowLevelSelectOnMenu = true; _gm.GoToMainMenu(); return; }
            SaveManager.SaveDesign(_editor.Design, out _);
            _gm.StartLevel(next, SaveManager.HasSave(next));
        }

        public void Replay() { _traffic.StartReplay(); }
        public void ShowSnapshot() { _traffic.ShowFailureSnapshot(); }

        public void ApplySettings(double deformationScale, bool showGrid, double sensitivity, bool fullscreen)
        {
            var s = _gm.Settings;
            s.DeformationScale = deformationScale; s.ShowGrid = showGrid; s.CameraSensitivity = sensitivity; s.Fullscreen = fullscreen;
            _gm.SaveSettings(); _gm.ApplySettings();
            _traffic.DeformationScale = (float)deformationScale;
            _camera.Sensitivity = (float)sensitivity;
            _building.SetShowGrid(showGrid);
            if (_testing) _traffic.UpdateVisuals();
        }

        private void OnTestFinished(SimulationResult r)
        {
            if (r.IsSuccess)
            {
                int prev = _gm.BestScoreFor(_level.Id);
                _gm.RecordCompletion(_level.Id, r.Score);
                _hud.ShowSuccess(r, _gm.NextLevelId(_level.Id) != null, prev);
            }
            else _hud.ShowFailure(r, _traffic.CanReplay);
        }
    }
}
