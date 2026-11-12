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
- 评分上不封顶：预算 : 时间 : 完整度 : 受力 = 6 : 2 : 1 : 1（用户 2026-11 调整）。预算分 = 6000 × 预算 / 花费；时间分 = 2000 × min(1, 理想完成时间 / 实际完成时间)，理想时间 = 每辆车按自身速度走完全程、船直接通过（不等待）；完整度分 = 1000 × 桥面完整度；受力分 = 1000 × max(最大利用率, 安全冗余)，安全冗余 = 1/利用率 − 1，上限 150%。用户确认“利用率越高越好”。
- 每种桥面在自身最大长度内（两端支撑）必须能承载规定车辆：木桥面要求轿车，钢桥面要求坦克，其余桥面要求重型货车（有回归测试）。每关至少有一种允许的桥面“跨中集中承载（吨）”不低于本关最重车辆；所有材料价格互不相同。
- 建材性能面板：悬停建材按钮显示 5 格进度条（简单），勾选“显示数值”显示具体数字（详细）；力统一用“吨”。
- 坡度：建议 ≤ 15%（容差 0.5 个百分点）；15%~30% 车辆减速爬坡；> 30% 爬不上去。
- 立柱有倾角限制（混凝土柱 15°、钢筋混凝土柱 30°、桥墩 5°），超限可建但测试开始时倒塌。
- 桥墩工具：从河床立起，自动生成墩底基础 + 中部 + 顶部支点；基础可选浅基础 / 桩基础（选中桥墩任一节点或墩身构件即显示基础按钮，LevelManager 用 PierBaseOf 找墩底）；桥墩显示为俯视胶囊形单柱（中间板 + 两侧圆柱，跨两侧桁架）；软土浅基础承载低；洪水冲毁木桥墩、掏空冲刷区浅基础。
- 旧桥加固：旧构件不计费、节点不能移动、最多拆 4 根、可付费修复。修复费 = 长度 × 单价 × RepairCostRatio（第十关 1.0）×（1 − 状况），即每份恢复的承载力与新建同价（2026-11 用户要求修复不能远比新建划算）；第十关预算 13500。会因横梁挡车的旧构件在打开设计时自动在中点刚性分段（CrossBeams.SplitExistingBlocking，只改内存，不改存档 / 起始桥文件）。横梁显示粗细为构件的 0.9 倍（≥0.15 m），挡车的显示红色。
- 限高（HeightClearance）：路面以上净空 4.5 m（本关最高车辆 + 0.3 m 更高时取后者），建造画面用橙色虚线和“限高”标出。两侧桁架之间的横梁只放在桥面及以下（横隔梁）或高于限高处（顶部横撑）；限高以内的节点、只连钢索 / 液压杆（材料 SideOnly）的节点不设横梁。
- 界面：右侧竖直“负载”条实时显示最大构件利用率（绿→黄→红，越红越接近承载极限；建造时为自重，测试时为实时）；左栏已无“删除所选”按钮（用 Delete 或多选后按 X）；构件按材料有不同截面（钢梁 / 预应力 I 梁为工字形、箱梁为空心箱、钢索 / 液压杆 / 混凝土柱为圆柱、钢筋混凝土柱为八角、钢 / FRP 桥面带纵肋、木桥面为横铺木板）并带程序纹理（MaterialManager.PatternTexture）。建造默认正交侧视；建造与测试底栏分开；失败弹窗先给一句话原因和建议，主按钮“定位问题并返回建造”。

## 性能
- 帧时间基准：Godot_v4.7.2-stable_mono_win64_console.exe --path . -- --bench level8,level7（Scripts/Core/Benchmark.cs）。逐关载入参考方案、切 3D 视角跑通车测试，关 vsync 采样 10 s，打印平均 FPS、p99、最差帧、draw call，超过 30 ms 的帧单独打印；运行期间 SaveManager.WritesSuspended=true，不写任何存档 / 进度 / 设置。
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
- 数据：CustomLevelSpec / CustomLevelBuilder（Scripts/Data/CustomLevel.cs，纯 C#，有测试）；保存在 user://custom_levels/{custom_名称}.json，删除只移到 deleted 子目录。GameManager.ResolveLevel 先找内置关卡再找自定义关卡；自定义关卡没有“下一关”。
- 命名方案：关卡菜单“方案另存为（命名）… / 读取命名方案…”，保存在 user://saves/{关卡}_named/，不影响原有自动存档 {关卡}.json；BridgeDesign.Name 记录名称。

## 关卡（12 关，按难度递增）
1 跨越小河 · 2 林间步道（胶合木限量）· 3 重型货车 · 4 高低两岸 · 5 有限锚点 · 6 坦克通行 · 7 航道净空（禁建墩 + 船）· 8 货运铁路 · 9 城市立交（双层）· 10 旧桥加固 · 11 软土与桩基（车队）· 12 洪水冲刷。

## 尚未实现（用户提过，留待以后）
钢—混凝土组合梁、弧形胶合木梁、温度伸缩、加固施工顺序（分段封闭）、双车道偏载 / 侧风 / 桥面扭转（需要横向受力计算）、自由建造模式。

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
