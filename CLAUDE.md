# Bridge Architect 3D — 项目上下文（供 Claude Code 在任何设备上接续开发）

Godot 4.7 .NET（C#，net8.0）3D 建桥物理模拟游戏。用户用中文交流，界面文字用中文；新写 / 修改的代码与注释用英文（用户 2026-11 要求）。

## 环境与命令
- Godot 4.7 .NET 编辑器；.NET SDK 8。`BridgeArchitect3D.csproj` 使用 `Godot.NET.Sdk/4.7.0`。
- 编译游戏：`dotnet build BridgeArchitect3D.csproj`
- 无界面验收测试（每次改动后必须跑，返回码 0 = 全部通过）：`dotnet run -c Release --project Tests/BridgeTests`
  - 用 Release 配置：Debug 输出曾被残留进程锁住。
- 修改数据（材料 / 关卡 / 车辆）后，必须确认所有关卡参考方案仍能通关（测试会自动检查）。

## 架构（纯逻辑与 Godot 分离）
- 纯 C#（不依赖 Godot，测试工程直接编译）：`Scripts/Data`、`Scripts/Physics`、`Scripts/Traffic/RoadPath.cs`、`TrafficEventSystem.cs`、`Scripts/Building/DesignEditor.cs`。
- Godot 层：`BuildingManager`（输入）、`BridgeRenderer`（显示）、`TrafficManager`（测试驱动 / 回放）、`VehicleController`、`ShipController`、`TerrainBuilder`、`GameHUD`、`LevelManager`、`GameManager`（Autoload）。
- 所有数值集中在 `Data/*.json`：材料 `Data/Materials/materials.json`，车辆 `Data/Vehicles/vehicles.json`，关卡 `Data/Levels/levels.json`，参考方案 `Data/ReferenceDesigns/levelN_reference.json`，起始桥 `Data/StartDesigns/`。

## 物理模型（StructureSolver）
- 二维刚架 / 桁架混合：梁、桥面、立柱为刚接梁单元（EA + EI）；钢索、液压杆为铰接二力杆。节点自由度 (ux, uy, θ)。
- 钢索受压自动松弛；欧拉屈曲；刚接构件按 N/Nu + M/Mu 校核；预应力 = 等效上托荷载；钢筋混凝土有开裂提示（不折减刚度）；旧构件按 Condition 折减承载力。
- 开启桥：BasculePlanner（Scripts/Physics/BasculeAnalyzer.cs）按拓扑自动识别——每个桥面接缝有两个端点（右侧键 = -节点号），候选接缝先按构件跳数、再按距离排序，所有液压杆整体匹配（同叶同转轴的杆协同），只分离需要的接缝；斜撑锁死时指出构件并停止驱动。BasculeSystem.Prepare 在建路径前把要分离的接缝拆成两个模型节点（StructuralModel.SplitNode），闭合时 Alias + AliasRot 刚性连接；桥叶刚体绕转轴转动，转轴处释放弯矩；每根驱动杆可收缩也可伸展，行程上限取材料 RetractRatio / ExtendRatio（液压杆 95%，不超过 LinkageDriverData.MaxStroke），并受 ActuatorForce 限制；候选组合按“能开到目标角 > 开启后挡航道构件最少 > 开得更远 > 原排序”选择（自定义关卡存档 Tests/BridgeTests/Fixtures/custom_level_*.json 回归：只在节点 9 断开、绕节点 8 抬起）；规划器与求解器节点身份统一（未分离接缝两端是同一节点，Canon），自动分配时每个候选组合都在真实模型上做稳定性检查和航道检查（ChannelBlocked），失败就试下一个组合；被斜撑锁死的液压杆写入 ConfigNote，其余桥叶照常工作。当前玩家存档回归：Tests/BridgeTests/Fixtures/level7_current.json（只读）。
- 没有有效机构时液压杆不动作（旧的自动收缩 25% 与“连接强度分离 / 变铰”都已删除，不允许用放宽容差制造通过）。BridgeDesign.Mechanisms（版本 2）只存开启角设置和旧版显式锁扣，版本 1 存档照常读取。
- 第七关货船高 9 m（必须开桥）；回归测试用玩家旧存档 Tests/BridgeTests/Fixtures/level7_bascule.json（只读，不能修改），验证旧存档能读、改造后行程不足被指出。
- 正交视图相机至少退到场景深度外（SceneryLayout.HalfDepth + 40 m），否则相机落在河岸土体内部、两岸被剔除看不见；缩放只由 Size 决定。
- 侧视 / 俯视（正交）固定米色背景（OrbitCamera.SideBackground），建造网格为深褐色半透明；右键拖动超过 12 px 才旋转，右键单击取消连接不会切到 3D。
- 固定步长 1/60 s、无随机数，结果必须可重复（测试 H 检查）。

## 已确定的游戏规则（用户明确要求过，不要改回去）
- 没有“创建节点”工具：构件必须从已有节点开始；删除构件后孤立节点自动清除。
- 长按节点（0.35 s 或移动 8 px）可拖动；删除工具直接点构件实体删除，空白处拖动框选。
- 节点吸附 / 拾取半径 0.25 m（DesignEditor.NodeSnapRadius），节点球半径与之相同；默认吸附最近节点，按住 Ctrl 只吸附网格。建造 / 删除模式双击右键回到选择 / 拖动；已多选时按 X 或点“删除”直接批量删除。
- 建材与工具是底部图标 + 文字平铺按钮；快捷键可在设置里自定义（数字键 1~0，GameSettings.MaterialKeys，未被占用的 1 = 选择）；“显示数值”在设置里（GameSettings.ShowMaterialValues）。
- 液压杆用滑块设定目标长度，范围由当前结构自动计算（DesignEditor.GetHydraulicRange：驱动桥叶时取 5°~85° 开启角内能达到的最短~最长长度，SetHydraulicTarget 换算成开启角；不驱动桥叶时：没有联动驱动就写入 BridgeMember.Stroke（可空，旧存档为 null = 材料默认收缩），仿真按它施加伸缩；已用联动模式时写联动驱动 Ratio）。拖动中实时更新，整次拖动只记一步撤销。黄色外筒长度 = 实际最短长度（DesignEditor.SleeveFractions：桥叶液压杆取 0°~目标角内最短长度，其余为 1+min(0,行程)）。旧存档行程原样保留，只夹到范围内。
- 液压杆费用随行程增加：费用 = 长度价 ×（1 + 0.8 × |行程|）（BridgeMember.CostStroke / EffectiveCost；新液压杆按默认收缩 95% 计价，旧存档无该字段时费用不变）；超预算照常允许（只影响评分）。
- 构件横梁（Scripts/Data/CrossBeams.cs）：长度 ≥ 5 m 的普通构件（非桥面 / 桥墩 / 钢索 / 液压杆）自动在中点加横梁，抗拉 +10%；柱类（ComponentType Column，桥墩除外）改为从柱底起每 10 m 一根横杆（≤10 m 没有，12 m 一根，25 m 两根，CrossBeams.Fractions）；横梁是实体，离路面高度在 (0.05 m, 车高) 内时车辆撞上即失败（VehicleBlocked）。用户 2026-11 明确要求“照常加横梁，车会撞”。受影响的参考方案（3/4/7/9/10/12）把挡车构件在中点一分为二，原文件备份在 Data/ReferenceDesigns/legacy/*_reference_v_before_crossbeams.json；旧夹具在测试里用 CrossBeamFixtures.SplitBlocking（刚性分段节点）适配，夹具文件不改。
- 复合钢索 cable_composite：比钢索更贵（460/m）、更长（60 m）、更粗、更重、拉力约 2.7 倍；第 5 关起开放。所有钢索自重计入模型，显示为抛物线下垂（重的下垂更多）。
- 测试底部进度条只在测试结束（成功或失败）后可拖动，进行中只显示：BridgeSimulation 记录整场时间轴（0.1 s 一帧），TrafficManager.Seek 显示对应时刻，按“继续”回到实时测试。
- 测试不会在 0 秒直接判负：问题先记录，不稳定部分先垮塌，车辆照常行驶；遇到断口卡住 3 秒判负；驶上断裂桥面才坠落。局部构件掉落不算致命。
- 预算可以超支，只影响评分。
- 所有关卡的地图锚点（支点）承载力无限（levels.json 里 MaxLoad = 0 表示无限）；只有桥墩地基（浅基础 / 桩基础）有承载上限。
- 评分 = 工程积分（Engineering Score，评分版本 2，见水下隧道一节的第 ⑧ 步），旧 6:2:1:1 已替换。
- 每种桥面在自身最大长度内（两端支撑）必须能承载规定车辆：木桥面要求轿车，钢桥面要求坦克，其余桥面要求重型货车（有回归测试）。每关至少有一种允许的桥面“跨中集中承载（吨）”不低于本关最重车辆；所有材料价格互不相同。
- 建材性能面板：悬停建材按钮显示 5 格进度条（简单），勾选“显示数值”显示具体数字（详细）；力统一用“吨”。
- 坡度：建议 ≤ 15%（容差 0.5 个百分点）；15%~30° 车辆减速爬坡；> 30°（tan30° ≈ 57.7%，RoadPath.MaxClimbGrade）爬不上去。所有车辆 MaxRampGrade = 30°（2026-11 用户要求，桥面与隧道统一；隧道关原来的 8% 特许已删除，旧数据备份 legacy/*_v_before_30deg.json）。
- 立柱有倾角限制（混凝土柱 15°、钢筋混凝土柱 30°、桥墩 5°），超限可建但测试开始时倒塌。
- 桥墩工具：从河床立起，自动生成墩底基础 + 中部 + 顶部支点；基础可选浅基础 / 桩基础（选中桥墩任一节点或墩身构件即显示基础按钮，LevelManager 用 PierBaseOf 找墩底）；桥墩显示为俯视胶囊形单柱（中间板 + 两侧圆柱，跨两侧桁架）；软土浅基础承载低；洪水冲毁木桥墩、掏空冲刷区浅基础。
- 旧桥加固：旧构件不计费、节点不能移动、最多拆 4 根、可付费修复。修复费 = 长度 × 单价 × RepairCostRatio（第十关 1.0）×（1 − 状况），即每份恢复的承载力与新建同价（2026-11 用户要求修复不能远比新建划算）；第十关预算 13500。会因横梁挡车的旧构件在打开设计时自动在中点刚性分段（CrossBeams.SplitExistingBlocking，只改内存，不改存档 / 起始桥文件）。横梁显示粗细为构件的 0.9 倍（≥0.15 m），挡车的显示红色。
- 限高（HeightClearance）：路面以上净空 4.5 m（本关最高车辆 + 0.3 m 更高时取后者），建造画面用橙色虚线和“限高”标出。两侧桁架之间的横梁只放在桥面及以下（横隔梁）或高于限高处（顶部横撑）；限高以内的节点、只连钢索 / 液压杆（材料 SideOnly）的节点不设横梁。
- 界面：右侧竖直“负载”条实时显示最大构件利用率（绿→黄→红，越红越接近承载极限；建造时为自重，测试时为实时）；左栏已无“删除所选”按钮（用 Delete 或多选后按 X）；构件按材料有不同截面（钢梁 / 预应力 I 梁为工字形、箱梁为空心箱、钢索 / 液压杆 / 混凝土柱为圆柱、钢筋混凝土柱为八角、钢 / FRP 桥面带纵肋、木桥面为横铺木板）并带程序纹理（MaterialManager.PatternTexture）。建造默认正交侧视；建造与测试底栏分开；失败弹窗先给一句话原因和建议，主按钮“定位问题并返回建造”。

## 性能
- 帧时间基准：Godot_v4.7.2-stable_mono_win64_console.exe --path . -- --bench level8,level7（Scripts/Core/Benchmark.cs）。逐关载入参考方案、切 3D 视角跑通车测试，关 vsync 采样 10 s，打印平均 FPS、p99、最差帧、draw call，超过 30 ms 的帧单独打印；运行期间 SaveManager.WritesSuspended=true，不写任何存档 / 进度 / 设置。
- 车辆模型与碰撞体积（2026-11 用户要求“模型一定和碰撞体积同大，避免穿模；碰撞体积不需要与模型完美重合”）：碰撞体积 = Scripts/Data/Definitions.cs 的 VehicleBody（沿路径 Rear~Front、宽 Width、高 Height 的盒子，BridgeSimulation.VehicleState 用同一套公式）。VehicleController.FitToBody 在建模板时测量每个部件的网格包围盒，超出部分按轴缩放进盒子（容差 2 cm），已在盒内的部件不动；坦克模型原点改到前轴（原来整体前移 2.9 m、炮管伸出车身 3.6 m），炮管缩到车身前沿。
- 坦克履带动画：CurvedMeshes.Track 生成 Track 节点（meta "loop" = 外轮廓，子节点 0 是履带带体，其余是均匀分布的履带板），VehicleController.AnimateRunningGear 按行驶距离让履带板绕轮廓转动（底部相对路面静止），负重轮（RoadWheel）同步滚动。带 meta "animated" 的节点不参与 LowPolyDetails.Batch 合并（否则会被合成一个静态网格）；Godot 会重命名重名节点，所以按 meta / 顺序查找，不按名字。
- 车辆模型：TrafficManager 每种车只建一次模板（关卡载入时 Prewarm），之后 VehicleController.SetupFrom 复制（共享网格与材质）；原来每辆车驶入时现场建模 40~75 ms，造成卡顿。
- 低画质（GameSettings.LowQuality，主菜单与关卡内设置都可勾选）：GameManager.ApplyQuality 关闭太阳阴影、关闭 MSAA、3D 按 75% 分辨率渲染（UI 不受影响）；TerrainBuilder 建灯时读 GameManager.ShadowsOn。基准加 --low 测低画质（只改内存设置），--shot <目录> 每关存一张截图。1080p 实测 GPU 1.64 → 0.66 ms，draw call 约减半。
- 显示模式与分辨率（GameSettings.WindowMode / Resolution，旧设置的 Fullscreen 自动迁移；UI 在 Scripts/UI/DisplaySettingsUI.cs，主菜单与关卡内共用）：窗口化 = 窗口大小（不超过屏幕可用区并居中）；全屏 = ExclusiveFullscreen；无边框 = Godot 的 Fullscreen（覆盖屏幕的无边框窗口）。全屏 / 无边框时分辨率低于屏幕则降低 3D 渲染比例（与低画质 75% 相乘），界面保持原生分辨率。Resolution 为空时不改窗口大小。在编辑器“游戏”标签内嵌运行（Engine.IsEmbeddedInEditor）时窗口归编辑器管，只应用 3D 画质，设置里显示提示。基准可加 --mode / --res（只改内存设置）。
- BridgeRenderer.SetMemberMaterial 材质没变就不重新赋值（MemberVisual.Applied）；测试状态栏每 0.05 s 刷新一次（LevelManager.UpdateTestHud）。

## 首页背景视频
- 主菜单背景是 6 分钟无缝循环视频（MainMenuVideo.cs）。按设置分辨率选文件（MenuVideoProfiles.cs：先找完全相同的，再找不小于目标且宽高比最接近的，缺文件时回退）；没有设置分辨率时按窗口大小选。
- 视频文件：Media/homepage/harbor-{1280x720,1366x768,1600x900,1920x1080,2560x1440,3840x2160}.ogv，1980x1080 用 Media/homepage-loop-forward.ogv。全部 Theora、30 fps、全关键帧（便于跳到同一时间点）。合计约 8.9 GB，导出前需要考虑体积。
- 切换分辨率时新视频先隐藏加载，解码出画面后跳到旧视频的同一播放位置再替换，不黑屏、不从头播放；GameManager.ApplyQuality 发出 DisplaySettingsChanged 信号触发刷新。
- 主菜单已不再使用视频背景（固定实时 3D，MainMenuVideo 代码与视频文件仍保留）；旧 homepage-loop.ogv 已删除。

## 铁路桥面
- 桥面材料 Surface：road（默认，只走汽车）/ rail（铁轨桥面 deck_rail，只走火车）/ both（铁轨公路复合桥面 deck_rail_road）。RailTrack（Definitions.cs）集中定义轨距、轨高、判定上限和 Allows(deck, vehicle)。
- BridgeSimulation：车辆前轴驶入不允许的桥面 → FailureReason.WrongSurface；CheckRailSway 每步计算列车每个车轴处铁轨的弹性下沉与竖向速度，超过 RailTrack.MaxSag / MaxBounce → VehicleState.Derailed + FailureReason.Derailed（MaxRailSag / MaxRailBounce 供测试读取）。
- 显示：铁轨桥面画两条钢轨（BridgeRenderer 桥面部件 2、3），有火车的关卡在两岸引道铺钢轨（TerrainBuilder）；火车整体抬高 RailTrack.Height，车轮按轨距摆放，轮底正好接触轨顶；脱轨动画 VehicleController.StartDerail（各节车厢依次侧翻滑出轨道）。
- 第八关只开放铁轨 / 复合桥面；参考方案桥面改为复合桥面（旧版备份 legacy/level8_reference_v_before_rail_deck.json）。

## 关卡设计模式（自定义关卡）
- 主菜单“关卡设计”（Scripts/UI/LevelDesigner.cs + LevelPreview.cs）：名称、河宽、两岸高度、水面 / 河床深度、预算、禁止建墩、船只与船高、可用建材、通行车辆、自定义固定节点（快捷按钮把节点吸附到左 / 右岸平地或岸壁坡面；河道中央的节点自动生成岩墩，坡面上的不生成）、岸壁角度（30°~90°，TerrainConfig.BankSlopeDeg / WallRun / GroundYInRiver：岸顶边缘仍在 x=0 / 河宽，坡面伸入河道，IsOnLand / IsInsideTerrain / PierBedY 都按坡面算；TerrainBuilder.AddWedge 画带碰撞的楔形土体）、河床材质（岩石 / 软土 → DefaultSoil + 覆盖整条河的 FoundationZone）、是否有水（HasWater=false 时 WaterLevel=河床，不生成水面，不能有船），实时侧视预览与检查。旧自定义关卡缺字段时读作 90° / 岩石 / 有水。
- 编辑器界面（2026-11 用户要求）：地图上的东西全部在侧视图里拖动圆点编辑（LevelPreview：左 / 右岸高度、河宽 = 右坡脚、岸坡 = 左坡脚、河床、水面、可建深度 / 高度虚线、固定节点（0.25 m 网格，靠近地面吸附）、船的航道与吃水）；双击空白加节点、右键删除节点 / 船；地图上方工具栏 = 有水 / 禁止建桥墩 / 河床岩石·软土 / ＋节点 / ＋船，下方检查器输入选中节点或船的精确值。非地图设置在下方分页 基本 / 建材 / 交通 / 高级（滑块、开关、切换胶囊）。地形按相对深度：河道深度 = 较低岸顶 − 河床，水深 = 常水位 − 河床。船：TrafficEvent.Draft（可空 = 船型默认，ShipCatalog.Draft），编辑器限制在船高 10%~50%；船可放在水面任意位置（LevelEditing.ShipLaneRange：按吃水处岸坡宽度，离岸壁 0.2 m），检查吃水范围、水深与碰岸。
- 关卡设计与关卡设置共用一个编辑器（2026-11 用户要求“两个功能完全一致、互补”）：Scripts/UI/LevelEditorPanel.cs（表单 + 预览 + 检查），宿主 LevelDesigner（新建 / 以内置关卡为模板 / 保存 / 保存并试玩 / 已保存列表）与 LevelSettingsUI（保存并重新开始 / 另存为自定义关卡 / 恢复默认）只加按钮。编辑操作与检查在 Scripts/Data/LevelEditing.cs（纯 C#，LevelEditTests）：改河宽 / 岸高时锚点跟随，河道中的固定节点自动配岩墩，禁止建墩区与河床土质覆盖整条河；Validate = LevelError + 设计器检查，所有内置关卡必须通过。
- 数据：自定义关卡文件现在存完整 LevelDefinition（user://custom_levels/{custom_名称}.json，删除只移到 deleted 子目录）；旧 CustomLevelSpec 文件由 LevelEditing.ReadCustomLevel 载入时转换（CustomLevelBuilder 仍用于新建默认关卡）。关卡内对自定义关卡“保存”直接写回该文件并删除旧版留下的 level_overrides 覆盖；内置关卡仍用覆盖。GameManager.ResolveLevel 先找内置关卡再找自定义关卡；自定义关卡没有“下一关”。
- 命名方案：关卡菜单“方案另存为（命名）… / 读取命名方案…”，保存在 user://saves/{关卡}_named/，不影响原有自动存档 {关卡}.json；BridgeDesign.Name 记录名称。

## 关卡（12 关，按难度递增）
1 跨越小河 · 2 林间步道（胶合木限量）· 3 重型货车 · 4 高低两岸 · 5 有限锚点 · 6 坦克通行 · 7 航道净空（禁建墩 + 船）· 8 货运铁路 · 9 城市立交（双层）· 10 旧桥加固 · 11 软土与桩基（车队）· 12 洪水冲刷。

## 尚未实现（用户提过，留待以后）
钢—混凝土组合梁、弧形胶合木梁、温度伸缩、加固施工顺序（分段封闭）、双车道偏载 / 侧风 / 桥面扭转（需要横向受力计算）、自由建造模式。

## 游戏版本号
- Scripts/Data/GameVersion.cs 的 GameVersion.Current（当前 1.1.0）是唯一来源；project.godot config/version、export_presets.cfg file/product_version（x.y.z.0）、installer/BridgeArchitect3D.iss AppVersion 必须一致，每次改版本都在 CHANGELOG.md 加条目（测试检查这四处）。修订号 = 修复，次版本 = 新内容且旧存档可读，主版本 = 不兼容。
- BridgeDesign / GameProgress / GameSettings 实现 IVersionStamped，写盘时 SaveManager 用 GameVersion.Stamp 写入 GameVersion；没有该字段的旧文件按 1.0.0。它和存档格式版本 BridgeDesign.Version（当前 5）是两回事。进度来自旧版本时 GameManager.UpdatedFrom 让主菜单提示一次；读到更新版本保存的方案时 SaveManager.VersionWarning 给提醒（照常载入）。主菜单标题下显示版本号。

## 工作约定
- 改动后：跑测试 → 编译游戏 → 更新 README 相关章节。
- Git：仓库 https://github.com/RonganBai/Bridge-Builder-Simulator ，分支 main；提交说明用中文或英文均可，结尾加 Co-Authored-By 行。推送前先征得用户同意。
- 没在 Godot 里实际运行过的改动，要明确告诉用户“未在 Godot 中验证”。

## 画面设置（GraphicsSettingsUI.cs，主菜单与关卡内共用）
- GameSettings：Antialiasing（off/fxaa/smaa/msaa2/msaa4/msaa8/taa，默认 msaa2）、Shadows（off/low/high）、RenderScale（0.5~1，<1 用 FSR）、Brightness/Contrast/Saturation（WorldEnvironment Adjustment；首页视频用 canvas_item 着色器）、VSync、MaxFps。预设 低/中/高，默认中（MSAA 2×、阴影低、100%）。GraphicsVersion=0 的旧设置由 MigrateGraphics 把 LowQuality 迁移为低预设。
- 首页视频分辨率上限 = 实际窗口大小（避免大视频缩小产生锯齿闪烁）。

## 打包发布
- 图标：icon.png（游戏内 / 项目）、icon.ico（多尺寸，exe 与安装包）。导出预设 export_presets.cfg（Windows Desktop，排除 Media / Tests / docs），需要 BridgeArchitect3D.sln（Godot .NET 导出必需）。
- 导出：Godot_console.exe --headless --path . --export-release "Windows Desktop" export/BridgeArchitect3D.exe（模板在 %APPDATA%/Godot/export_templates/4.7.2.stable.mono）。
- 安装包：%LOCALAPPDATA%/InnoSetup6/ISCC.exe installer/BridgeArchitect3D.iss → export/installer/BridgeArchitect3D-Setup-1.0.0.exe（export/ 不入库）。

## 加载速度（主菜单）
- LowPolyDetails.Batch 直接在 C# 合并顶点数组（PrimitiveMesh 用 GetMeshArrays 在 CPU 生成，相同参数的图元缓存；CurvedMeshes 建的网格保存数组，CurvedMeshes.TryGetArrays），不再逐个 SurfaceTool CreateFrom/Index/Commit（那样会向渲染器注册临时网格并回读）。树木用单位尺寸网格 + 节点缩放。主菜单背景构建 6.5 s → 约 1.1 s。
- MainMenuBackdrop：首次进入主菜单时先显示界面，2 帧后再构建背景；车辆 / 船只模型之后每帧建一个。进入关卡前 GameManager.StartLevel 调用 MainMenuBackdrop.KeepFrom 把背景从场景摘下保存在内存，回到主菜单直接复用（约 0.25 s）。
- 引擎自身启动（Vulkan、着色器缓存、.NET）约 3 s，不是游戏代码；导出版首次运行还要编译着色器管线，会更慢。

## 语言（中文 / English）
- Scripts/Data/Loc.cs（纯 C#）：源文本就是中文字符串，Loc.T("中文") / Loc.F("中文 {0:0.0}", 值) 查 Data/Locale/en.json（中文 → 英文）；缺条目时显示中文。GameSettings.Language（"zh" 默认 / "en"）。
- GameManager._Ready 先读设置、设 Loc.Language、载入 en.json，再用 Loc.TranslateJson 翻译关卡 / 材料 / 车辆 JSON 里的文字后建 GameDatabase。
- 语言选项只在主菜单设置里（“语言 / Language”，双语标签）；切换后 GameManager.RestartForLanguage 保存设置并自动重启（静态文字只在启动时生成）。
- 新增中文界面文字后：node tools/i18n/locwrap.js .（把未包裹的中文字面量改成 Loc.T / Loc.F），再 node tools/i18n/missing.js 列出缺英文的条目，补进 en.json（占位符 {n}、\n、[color] 标签必须一致）。“语言 / Language”“中文”两个是故意不翻译的。
- 英文文字更长：设置对话框英文时加宽，失败标题自动换行。

## 导出版用户数据
- project.godot 的 config/use_custom_user_dir.template / custom_user_dir_name.template：导出版（template 特性）存档在 %APPDATA%/BridgeArchitect3D，和编辑器 / 开发运行的 %APPDATA%/Godot/app_userdata/Bridge Architect 3D 分开，所以安装版从零开始，不带开发时的关卡进度。不要删除开发目录里的存档。

## 水下隧道（Scripts/Data/Tunnels.cs + Scripts/Physics/TunnelAnalysis.cs，第十三关）
- 2026-11 用户给了完整的“桥梁与隧道系统 + 工程评分”规格，按其 10 步顺序分多轮做：① 统一地面起终点与道路（已完成）② 地下入口与自动坡道（已完成：直线坡道 + 竖曲线 + 坡道计费 + 河岸加长；3D 弯曲坡道、转弯半径未做）③ 沉管（已完成，按新规格重调）④ 悬浮隧道（已完成，第十四关）⑤ 盾构（已完成，第十五关）⑥ 统一天气（已完成，见下）（正常 / 暴雨 / 严重洪水：水位 0/+2/+5 m，流速 0.5/0.8/1.1 m/s，地下水 0/+0.5~1/+1~3 m，土体软化 0/10/25%；不能直接降低材料强度）⑦ 四阶段测试（已完成，见下）⑧ 工程评分（已完成）⑨ 工程合同 + 优秀方案记录 + UI（地下剖切 / 跟车视角）（已完成，见下）⑩ 平衡与回归（已完成，见下）。洞口防洪（抬高入口 / 防洪闸 / 排水泵）、承压上限：盾构 0.28/0.38/0.50 MPa、悬浮 0.09/0.16/0.24 MPa。
- 用户决定（2026-11）：新坡度规则（车辆 MaxRampGrade：普通 6%、货车 5%、满载重卡 4.5%、半挂 / 坦克 4%；关卡 Objectives.TunnelMaxGrade 可特许，如第十三关 8%）只用于隧道坡道与管身，桥面仍按 15% / 30% 规则。
- 评分（第 ⑧ 步，已完成）：用户要求“无上限工程积分（Engineering Score）”替代所有关卡的旧 6:2:1:1 评分，不用 100 分满分制：参考方案约 1000 分但不封顶；经济性权重最高、非线性；必须通过全部车辆与天气测试才有正式分；安全冗余奖励到合理水平封顶；工程成就倍率（极端洪水、额外重载、航道保护、低环境扰动、高可靠性，关卡预配置、每项只算一次，不用星星 / 收集品）；多维排行榜（综合 / 最低造价 / 最快通行 / 最高安全裕量，按评分版本区分）；保存每关最高分及最低成本 / 最快 / 最可靠方案；桥梁与各种隧道统一标准，特殊成本与环境影响全部计入；权重可配置。
- 工程积分实现（Scripts/Data/EngineeringScore.cs + Data/Scoring/scoring.json，权重与系数全部可配置）：基础分 = 1000 × Σ 权重 × 系数 × 桥面完整度 / Σ权重；经济 0.45 ×(参考造价 / 造价)^1.5（参考造价 = 关卡 Objectives.ReferenceCost，即参考方案造价；没有时预算 × 0.9），安全 0.2 × min(1.25, 裕量 / 0.3)，天气 0.15（天气期间裕量，同公式；无天气按安全），通行 0.15 × 理想 / 实际时间，环境 0.05 × max(0.2, 1 − 扰动 / 河宽)（河中桥墩 6 m / 个、沉管长度、悬浮锚索 0.5 m / 根、盾构 0）。参考方案 819~1109 分（测试要求 800~1200）。成就（EvaluateAchievements，通过后在 LevelManager 调用，每项只乘一次）：extreme_flood（水位 ×1.4 重测）、heavy_load（追加 truck_heavy 重测）、channel_protection（船只通过且不用开桥）、low_disturbance（环境系数 ≥ 0.8）、high_reliability（利用率 ≤ 70%）；Objectives.Achievements 为空时按关卡条件自动选：洪水 → 极端洪水，船 → 航道保护，本关已有 ≥ 0.5 × 满载重卡质量的公路车辆才给额外重载（只有轿车的第一关不给，避免不公平），另加低环境扰动、高可靠性。记录：GameProgress.Records[关卡] = LevelRecord（Version、BestScore、LowestCost、FastestTime、BestMargin），版本不同就重新记录；刷新的项目把方案存为命名方案“★ 最高综合分 / ★ 最低造价 / ★ 最快通行 / ★ 最高安全裕量”。BestScores 只用于解锁（旧版本分数不动），关卡菜单显示工程积分最高分，旧存档只显示“已通关（旧评分）”。
- 数值全在 Data/Tunnels/tunnels.json（管节长度、风险阈值 70/90%、土质、坡道单价、每种隧道类型的尺寸与 3 个等级：费用、质量、水压上限、接缝转角、支点承载）。建材 tunnel_immersed（TunnelType "immersed"）。
- 方案字段 BridgeDesign.Tunnel（TunnelRoute：材料、控制点节点 PointNodeIds、每节 Grade / Reinforced、Cost），存档版本 5。版本 4 的隧道段由 TunnelRules.MigrateLegacy 合并成一条路线（tunnel_rc→加强、tunnel_steel→重型、其余→普通）；旧参考方案备份在 legacy/level13_reference_v_before_tunnel_types.json（测试用它验证迁移）。
- 建造：隧道工具在河底点起点再点终点；已有隧道时点击管身插入控制点（BridgeNode.TunnelPoint），拖动控制点改埋深（最多挖 MaxTrenchDepth，开挖按深度计费）。DesignEditor.RegenerateTunnel 按路线长度重新分 10 m 管节，接缝处自动生成支点（TunnelJoint = 接缝序号，在管顶，不能移动 / 单独删除，只接 Concrete 建材，承载取两侧较弱等级，软土减半）。选择工具点管节 → 属性面板显示水压 / 地基 / 沉降 / 接缝分项和等级按钮、加固地基、“应用到全部管节”。
- 沉管荷载模型：水压 = ρg(水位 − 管底)，洪水抬高水位；地基压力 = (自重 − 浮力 + 车轴力)/(宽 × 长)，对比土质承载 × 加固系数，冲刷区洪水时 × ScourFactor（加固或埋深 ≥ ScourCover 免疫）；沉降 = 系数 × 利用率；接缝转角 = 相邻沉降差 / 跨度。任一项 ≥ 100% → 隧道进水（FailureReason.TunnelFlooded，每种原因有自己的文字）。
- 显示：BridgeRenderer 每节一个半透明管身，按风险着色（绿 / 黄 / 橙 / 红），建造时为静态工况，测试时 LevelManager.UpdateTestHud 实时刷新并把沉降放大 10 倍显示。
- 贯通两岸且管身坡度不超过 TunnelRules.RampGrade（最弱车辆或关卡特许）时，主路公路车辆走隧道（TunnelRules.Lane），火车仍走桥。
- 地面道路：TunnelRules.GroundStartX / GroundEndX 是主路固定起终点（有隧道建材的关卡按最深可能管底的坡道长度外推），桥面路线（RoadPath.Build，lane 0）和隧道路线都从这里出发，没有隧道的关卡仍是锚点外 25 m（不变）。坡道长度 = 高差 / 坡度，转折处用抛物线竖曲线（TunnelRules.SmoothGrades）。TerrainBuilder 按 TunnelRules.BankExtent 加长两岸。坡道费用 = 坡道水平长度 × RampCostPerMeter，计入隧道费用。
- 沉管承压（新规格）：普通 / 加强 / 重型 0.14 / 0.22 / 0.30 MPa。
- 悬浮隧道 tunnel_floating（tunnels.json Types.floating，Placement "water"，HasSupports false：没有接缝支点、不能建桥）：控制点 = 管底，范围 [河床 + MinBedClearance, 水面 − MinCover − 管高]，新建时尽量浅（坡道短）。每节可设浮力等级（Ratio = 浮力 / 自重，配重自动匹配；低 1.04 / 中 1.2 / 高 1.35）、锚索根数（0~6）与种类（钢绞线 / 高强复合，每根承载 N）、加固锚固基础（Reinforced）；TunnelElement.CableCount / Cable / Buoyancy 可空（旧存档 = 类型默认），存档仍为版本 5。
- 悬浮荷载模型（TunnelAnalysis.Floating）：浮力按浸没体积（完全浸没时洪水不加浮力，只加水压）；净上浮 = 浮力 − 自重，车辆超过它 → 锚索松弛下沉；水流横推 ½ρCdHv²（流速 TunnelConfig.CurrentNormal → CurrentFlood，按 BridgeSimulation.FloodFraction 插值）；每根锚索 = |(竖向, 横推)| / 根数；锚固基础按土质（洪水冲刷区不加固 × ScourFactor）；横向摆动 = 横推 / 横向刚度（上限 MaxDisplacement）；车辆下压位移检查接缝（两端与河岸固定）。每种原因有自己的失败文字。显示：锚索与锚块，测试时管段按摆动横移、按下压放大下沉。
- 盾构隧道 tunnel_shield（Types.shield，Placement "ground"，无支点）：控制点 = 管底，范围 [max(河床 − MaxDepthBelowBed, 关卡 BuildMinY), 河床 − 管高 − 1]，新建时覆土 DefaultCover 6 m。所有隧道类型的最深位置都受关卡 BuildMinY 限制（同时决定固定地面起点的距离）。地层：TerrainConfig.Strata（GeoColumn X0~X1 + GeoLayer 从河床往下的 Soil / Thickness，0 = 往下一直是它），没有时按 FoundationZones（岩石 / 黏土）；参数在 tunnels.json Geology（rock / clay / sand / silt：MinCover 3/6/9/12、Gamma、EarthK、Permeability、LagTime、ExcavationCost）。Reinforced = 注浆加固（最小覆土 × GroutCoverFactor、土压 × GroutEarthFactor）。费用 = 管片 + 按轴线地层的掘进费 + 注浆 + 坡道。
- 盾构荷载模型（TunnelAnalysis.Shield）：稳定 = 管顶所在地层最小覆土 / 实际覆土；外压 = 地下水压 ρg(地下水位 − 管底) + 有效土压 K·Σγ'h，两项分别显示，管片按总外压校核。地下水位 = 常水位 + 地下水上涨（不再另加河水压力，避免重复）；BridgeSimulation 每步按管身穿过的最透水地层，把上涨量以 LagTime 为时间常数追向 Permeability × 洪水上涨（砂土快、黏土慢）。显示：盾构管身不做深度测试（透过土体看到），TerrainBuilder.BuildStrata 在建造平面画地层色带与名称。
- 生态保护区（NoBuildZones）里不能建沉管或悬浮隧道（扰动河床），盾构可以从下面穿过；水太浅时不能建悬浮隧道。
- 第 ⑩ 步：AUDIT=1 运行测试会打印每关参考方案的造价比、利用率、天气裕量、时间、环境系数、分数、成就与可选合同（平衡表）。隧道关路线 740~1060 m（最深管底的坡道 + 入口最大抬高决定地面起点），整场 187~240 s：BridgeSimulation.QuietTraffic（静态阶段后、桥上与管身无车轴、无天气、无船、开启桥不动）时 TrafficManager 自动 ×8 播放（AutoFast，测试栏“引道快进”开关，只改播放速度不改结果），玩家实际观看约 67~79 s。通关后的成就重测在后台线程运行（LevelManager.OnTestFinished 用 Task.Run，测试里检查并行与顺序结果一致）。
- 工程合同（scoring.json Contracts，EngineeringScore.ContractsFor / ContractConfig）：低成本（造价 ≤ 参考 × 0.85）、高安全（裕量 ≥ 50%）、极端天气（要拿到极端洪水成就）、重型物流（要拿到额外重载成就）、环境保护（环境系数 ≥ 0.8）；每个合同换一套权重，满足条件总分 × Bonus（1.2~1.25），不满足仍通关、只是没有奖励；需要某成就的合同只在本关提供该成就时出现。BridgeDesign.Contract（可空，旧存档为 null，存档版本不变）；建造左栏“工程合同”下拉框选择。记录按合同分开（EngineeringScore.RecordKey = 关卡 # 合同），并多一项最低环境影响（LowestDisturbance，“★ 最低环境影响”命名方案）；关卡菜单卡片显示最低造价 / 最快 / 最高安全裕量 / 最低扰动。
- 地下剖切：TerrainBuilder 把两岸土体、草皮、岸壁、河床、水体放进组 TerrainBuilder.CutawayGroup，LevelManager.SetCutaway 隐藏 / 显示；建造左栏与测试栏都有开关。跟车视角：测试栏开关，OrbitCamera.Follow 每帧对准 TrafficManager.LeadVehiclePosition（最前面还在行驶的车），滚轮仍可缩放。基准可加 --cutaway / --follow 截图。
- 统一天气（Scripts/Data/Weather.cs + Data/Environment/weather.json，桥梁与隧道共用）：预设 normal / rainstorm / flood（WaterRise、Current、GroundwaterRise、SoilSoftening、PondRate、Scour）。环境事件 TrafficEvent.Weather（+ 可选 Rise）；旧事件没有 Weather = 严重洪水、水位用 Height（旧关卡 / 存档照常）。BridgeSimulation 每步取所有活动事件各项最大值（按 RampTime 渐变）：FloodRise、Current、GroundwaterDirect（加到盾构地下水目标）、Softening（沉管地基 / 悬浮锚点承载 ×(1−s)，盾构所需覆土与土压 ×(1+s)，桥墩地基与隧道支点 AnchorMaxLoad ×(1−s)；材料强度不变）、PondRate；只有 Scour 预设冲刷。
- 洞口防洪（TunnelRoute.PortalRaise / FloodGate / Pump，两岸同时，DesignEditor.SetPortalProtection，HUD 隧道属性行 −/+、防洪闸、排水泵）：入口 = 岸顶路面 + 抬高（先以同坡度爬上去再下坡道，多出的坡道按坡道计费 + RaiseCostPerMeter）；洪水高于入口（有闸时 + GateHeight）→ 隧道进水；有闸时水位接近入口就关闸，关闸或积水 > PondBlockDepth 时隧道车道车辆在左入口前等候（WaitingAtPortal，不算失败）。积水 += (PondRate − 泵 − 雨停后自然排水)·dt。第十三~十五关洪水水位比岸顶高约 1 m，参考方案都抬高入口 1.5 m（旧版备份 legacy/level1x_reference_v_before_weather.json）；第十五关开局有暴雨（没装泵时车辆在入口等积水退去）。
- 四阶段测试（TestPhase，BridgeSimulation.Phase）：① 静态 = 开头 StaticPhaseTime 1 s 只有自重和正常天气（关卡所有事件在 TestEvents 里整体后移 1 s，理想时间同样后移，评分不变）；② 车辆；③ 环境 = 有天气事件活动时（与车辆重叠时算环境）；④ 结果。SimulationResult 新增 FailurePhase、PhaseMaxUtil[3]（−1 = 未进行）、WeakestMemberId / WeakestText / WeakestX（构件或隧道管节）、MaxDeckSag、MaxTunnelMove、WeatherMargin（天气期间 1/利用率 − 1）。成功 / 失败弹窗都显示 GameHUD.PhaseReport；测试状态栏前面显示当前阶段。超时上限 = max(240 s, 2 × 理想时间 + 60 s)（深隧道长坡道 + 入口等候）。
- 第十五关：60 m 宽、8 m 深，整条河生态保护区，地层 淤泥 4 / 砂土 5 / 黏土 6 / 岩石；洪水 +5 m；坡道特许 8%；BuildMinY −30。浅埋在砂层要注浆且洪水时普通管片外压超限；参考方案 = 管顶进黏土（管底 −28.5 m）+ 加强管片（$146,550 / 预算 170000）。
- 第十四关：80 m 宽、30 m 深、全软土、30~50 m 冲刷、洪水 +3 m、坡道特许 8%；沉管重型也扛不住水压（测试检查），低浮力被半挂车压沉，高浮力拉坏软土锚点，2 根锚索洪水时摆动过大。参考方案 = 加强管节 + 中浮力 + 每节 4 根钢绞线 + 全部锚点加固（$189,225 / 预算 205000，利用率 71%）：统一天气后洪水软化软土 25%，原方案（3 根、只加固冲刷区）利用率 98%，已备份 legacy/level14_reference_v_before_softening_rebalance.json。
- 第十三关：水深 10 m、洪水 +5 m、坡道特许 8%、岩石 / 软土 / 冲刷岩石三区；普通管节洪水时水压 147 kPa 超限，软土交界不加固会漏接缝，冲刷区要加固或埋深 ≥ 2 m。参考方案 = 全部加强管节 + 20~60 m 加固地基（预算 105000）。

## 天气画面与地震（2026-11 用户要求）
- WeatherPreset 新增 Rain / Overcast（只影响画面）和 QuakeWiden / QuakeAccel / QuakeFrequency；weather.json 新预设 earthquake（河道永久变宽 0.3 m、0.15 g、1.2 Hz）。关卡编辑器的天气下拉自动列出它；目前没有内置关卡使用地震。
- 物理：河道变宽 = 固定支点的规定水平位移（StructuralModel.SupportShiftX，右岸全量、河中基础按 x / 河宽线性分摊；StructureSolver 把 K·u 移到右端，固定自由度输出规定位移），随事件 2 s 渐入、结束后保持；摇晃 = 全部质量的水平惯性力（StructuralModel.GroundAccel，LoadCalculator）。两岸固定的刚性桥面会被 0.3 m 拉断（QuakeTests 检查）。
- 画面：Scripts/World/WeatherVisuals.cs（GPUParticles3D 倾斜雨滴、天空变灰、太阳变暗、轻雾），地震时相机 HOffset / VOffset 抖动；SimFrame 记录 Rain / Overcast / Shake，拖动回放同样显示。

## 隧道电梯（2026-11 用户要求，覆盖上面关于坡道 / 洞口防洪 / 漏水的旧规则）
- 入口 = 两岸垂直电梯（材料 lift，IsLift，不能单独建造；电梯结构是玩家构件，见下文“电梯 = 普通构件”）。参数在 C# LiftConfig（TunnelConfig.Lift）：平台 6~26 m、速度 2.5 m/s、承载由构件决定（见下文）。TunnelRoute.LiftLength 可空（null = 最长）；DesignEditor.SetLiftLength / LiftLengthForLevel（自动配）。
- 路线：RoadPath.LiftSegments 标出电梯段；BridgeSimulation.LiftState 一次运一辆，车停平台、LiftDy 动画、到达后 FrontS += Shift；太长 → VehicleBlocked，太重 → FailureReason.LiftOverload。理想时间按电梯排队（乘坐 + 空载返回）计算。
- AddTunnel 一键从岸壁建到岸壁；两端 x 锁定在岸壁，只能上下；旧存档载入时贴合岸壁，PortalRaise / FloodGate / Pump 忽略且不计费。
- 隧道失败只看材料强度：沉管 = 水压，悬浮 = max(水压, 锚索)，盾构 = 水压 + 土压；UpdatePortals 不再进水。参考造价：第十三关 $89,920、第十四关 $161,695、第十五关 $158,620（重型管片）。
- 土质（2026-11 用户要求）：隧道不受土质影响，只有盾构管片的土压随地层 / 注浆 / 软化变化；沉管地基、悬浮锚点、隧道支点都与土质无关（SupportCapacity = 等级承载，天气软化只削弱桥墩地基）；只有盾构注浆计费，旧存档沉管 / 悬浮的 Reinforced 照常读取但不计费。
- 沉管（TunnelTypeConfig.Straight）：整节预制直管，不能加控制点，只能上下拖动两端；旧存档的中间控制点载入时删除。价格 普通 / 加强 / 重型 500 / 1150 / 1950（基础价最便宜，升级比其他隧道贵）。旧 tunnels.json 备份 legacy/tunnels_v_before_straight_immersed.json。
- 接缝圆点（2026-11 用户要求）：盾构 / 悬浮隧道的可拖动点就是管节接缝（TunnelRules.JointXs 从岸壁到岸壁等分，每节 ≤ ElementLength），每节 = 相邻两点之间的直段；接缝点只能上下拖，不能增删；旧存档和参考方案在 TunnelGeometry.Build / DesignEditor.SnapPointsToJoints 里按原路线重采样。渲染 BridgeRenderer.MiterTube 把每节两端剪切到接缝角平分面（以管底接缝为轴），相邻管节无缝隙、无重叠；沉管仍是直管（只有两端）。
- 车辆制动（2026-11 用户要求“等候停车要缓慢停住”）：VehicleState.Speed，BridgeSimulation.VehicleBrake 3 m/s² 减速、VehicleAccel 2 m/s² 起步；每步先向前探测一个制动距离找停车线（开启桥、电梯、前车），按 √(v前² + 2a·距离) 限速，实际位移永远不越过停车线。LiftQueueTests 检查减速不超过约 2 倍制动率。
- 电梯等候：平台不在时车辆车头停在平台前 TunnelRules.LiftWaitClearance（1.2 m），避开着陆门与井道端墙（端墙中心在平台外 LiftEndWallOffset 0.45 m、厚 0.25 m，渲染与仿真共用常量）；原来 0.5 m 时车头插进端墙（2026-11 用户报告“等候电梯时前进穿模”）。LiftQueueTests 检查排队车辆不越过端墙、不重叠、等候时不移动。
- 电梯显示：井道与轿厢封闭（AddEnclosure），门两侧空档有固定栅栏，配重加宽；剖视时前侧墙板（_tubeFrontDetails）和前侧装饰（TerrainBuilder.CutawayFrontGroup，z > 0 的树 / 房子）隐藏。

## 桥面档次（承载 / 自重 / 成本，5 档）
木 2/1/1 · 钢格栅 deck_grating 3/2/3 · 钢筋混凝土 road 4/5/2 · 预应力混凝土 deck_psc 5/4/3 · 加劲钢 deck_steel 5/2/4 · FRP 3~5/1/5（有回归测试）。格栅开放在第五关，预应力在第十一、十二关。

## 结构化隧道与电梯（2026-11 用户规格，覆盖上面关于沉管支点、悬浮锚索根数、电梯承载 = 平台长度的旧规则）
- 沉管 / 悬浮：Scripts/Physics/TunnelStructure.cs 把管身作为梁链加入普通求解器（每节一根，EI = 管身与一道接缝串联，抗弯 = JointStiffness × JointRotation），两端铰接在电梯井，中间接缝是自由节点（BridgeNode.TunnelJoint，在管底），玩家构件在接缝处铰接。净重 = 自重(+配重) − 浮力，悬浮配重按 浮力 / 浮力比 一次算清，不重复计。Util = max(管壁水压, 接缝弯矩)。横向摆动是标注为简化的显示项。
- 有非盾构隧道时水下可建节点（河床以上、管身以外）；河床端点自动成为墩底浅基础（DesignEditor.IsBedPoint）。旧锚索按钮停用（LegacyCableButtons = false）。
- 旧存档迁移 MigrateStructuralTunnel（TunnelRoute.Structure == null → 1）：沉管每道接缝一根 pier_concrete（浅基础不够就桩基础），悬浮按旧根数生成真实钢索 + 锚块。只运行一次。
- 电梯 = 普通构件（2026-11 用户要求“像组装桥梁一样，不在信息页点击；换材料 / 改设计模型要变化；隧道编辑要影响电梯”，覆盖之前的参数式电梯）：
  - 固定节点 BridgeNode.LiftBank（1 左 / 2 右）+ LiftRole：deck0 (X0, Bottom)、deck1 (X1, Bottom)、两个 pit (X0−ColumnOffset / X1+ColumnOffset, Bottom−Pit)。IsLiftFixed 属于 IsTunnelNode：不能删；只有外端（左岸 deck0、右岸 deck1）能横向拖动 → SetLiftLength。ColumnRules 的 Land 基础接受 LiftRole == "pit"。
  - LiftStructure.Analyze：按 LiftBank 或井道区域 InZone 归属节点；平台 = 从 deck0/deck1 经非钢索构件 BFS；Ties = 靠端墙面 TieTolerance 内的非平台节点（FixX，每处 BraceCost）；Hoists = 一端在平台的钢索；Sheaves = 吊索上端；DeckPath = 桥面构件从 deck0 到 deck1。Problem：缺端点 / 桥面不通 / 平台刚接井道 / 无吊索。
  - ApplySupports（StructuralModel.Build 也调用，主模型里电梯构件同样受自重和配重）：先清掉井道内节点的陆地基础（井道是挖空的：否则中间柱节点会白拿支撑，2026-11 发现并修复），再 pit 固定 + 锚点、deck0 FixX（导轨）、Ties FixX、配重按绳数作为 NodeLoadY 挂在滑轮节点。
  - Check(design, level, db, L, axles)：只取该电梯的子结构单独求解（平台在下层站台位置分析），轴荷按 DeckPath 放到平台桥面；MemberUtil 逐根，驱动 = |吊索总拉力 − 配重| / 驱动力，BaseReaction = 井底竖向反力。
  - LiftDesign 只保留模板来源（Deck/TrussDepth/Panel/Web/Chord/Diagonal/Hoist/Column）+ Drive + Counterweight + Built（null = 旧存档）。模板：平台桁架为 Pratt 式（斜杆从吊点向跨中向下，悬吊平台始终受拉，用钢索），柱子分段 ≤ ColumnSegment 4.5 m（< CrossBeams.MinLength，不会在上层站台产生挡公交车的横梁；同时缩短屈曲长度），每段节点靠端墙被拉结。light 也要浅桁架（求解器节点只有平动自由度，纯桥面链吊两端是机构）。
  - DesignEditor：GenerateLifts / RebuildLifts(template?)（HUD「重建电梯结构」= liftauto：先按车身长度定平台，再选最便宜的够用模板，LiftTemplateForLevel 在编辑器副本上逐个模板生成并校核）、SetLiftMachine（HUD 选中滑轮节点时的 SetSheaveRow："liftset:drive:id" / "liftset:cw:±n"）、SyncLifts（RegenerateTunnel 每次调用：固定节点跟随井道；平台范围内的自由节点按比例拉伸 x，两侧跟随较近端；下半部跟随站台高度）、MigrateLiftStructure（Built == null → 生成一次，Built = 1，重复载入不变）、PreviewTunnel 在副本上 AddTunnel 求真实总价（_previewing 防递归）。
  - 费用：构件按材料单价；TunnelRules.Cost 另加每部 驱动 + 配重 × CounterweightCostPerTonne + 井道高度 × ShaftCostPerMeter；RegenerateTunnel 加 Ties × BraceCost。
  - 仿真：LiftState.Layout；BuildLifts 时有 Problem → LiftOverload 失败（写明原因与改法）；LiftGate 的 CheckLift 把车轴换算到平台 x（X0 + stop + offset − EntryS）。
  - 渲染：实际电梯只画井道围护、站台门、轿厢装饰（门、侧墙、扶手）和滑轮下的配重（吨位决定大小，轿厢上升时配重下降）；平台构件节点在 UpdateTransforms 里加 LiftOffset（随轿厢升降，吊索自然伸缩）。未建隧道前的虚影预览仍用旧示意模型。
  - 允许材料：第十四、十五关加了 concrete（模板井道柱）。
- 参考方案（Tests: --gen-tube-refs --write 重新生成）：第十三关 加强沉管 + 接缝 2/4 桥墩 + 标准模板电梯构件 $212,828 / 预算 223000；第十四关 加强 + 低浮力 + 接缝 2/4/6 锚索 + 标准电梯 $253,369 / 282000；第十五关 原盾构方案 + 标准电梯 $294,108 / 315000（预算按原来的余量比例放大）。备份 legacy/*_v_before_structural_tunnels.json、*_v_before_structural_lifts.json、*_v_before_member_lifts.json。
- 测试：TunnelStructureTests、LiftStructureTests（--lift-only；--lift-sweep 打印第十三~十五关每个模板的费用与最不利工况）。已知失败：第七关“35% > 30°”断言（单独报告，不改爬坡规则）。
- 未完成：盾构洞口与竖井设计未做；en.json 还有 108 条与电梯无关的旧缺失（车辆说明等）。


## 当前机构电梯规则（优先于旧版模板电梯说明）

- `LevelDefinition.MechanicalLift` 用于固定通道电梯关。关卡提供井道开口、少量安装座及平台连接点，不自动生成平台和驱动；旧模板电梯仅保留给普通关卡沙盒和明确请求的参考示例。
- `LiftMechanics` 校核竖直液压驱动行程、固定滑轮连续钢索、独立配重端点和安装支架稳定性。滑轮串联绳索使用耦合刚度、等张力，配重和滑轮质量、固定支点承载均计入求解。
- 载荷按下层 / 半程 / 上层的二维准静态工况校核。电梯缺路面、无驱动、行程不足或损坏时停止测试，禁止继续放行；普通桥梁延迟判负规则保持。
- 定滑轮和配重是节点设备，通过建材工具点击安装，Shift 点击拆除 / 减重，均计费且可撤销。受力显示穿透井道；不稳定或松弛为灰色，不能伪造有效受力值。
- 上传前已同步 README 的新入口、旧模板适用范围、受力与经济评估操作。没有网页帮助中心，未创建替代帮助页面。
