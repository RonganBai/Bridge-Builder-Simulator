# Bridge Architect 3D — 项目上下文（供 Claude Code 在任何设备上接续开发）

Godot 4.7 .NET（C#，net8.0）3D 建桥物理模拟游戏。用户用中文交流，代码注释、界面文字、提交说明都用中文。

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
- 液压杆：船只通行前 4 s 自动收缩 25%，船过后恢复（Prestrain 强制伸缩）。显示时液压位移按真实比例，弹性变形才放大。
- 固定步长 1/60 s、无随机数，结果必须可重复（测试 H 检查）。

## 已确定的游戏规则（用户明确要求过，不要改回去）
- 没有“创建节点”工具：构件必须从已有节点开始；删除构件后孤立节点自动清除。
- 长按节点（0.35 s 或移动 8 px）可拖动；删除工具直接点构件实体删除，空白处拖动框选。
- 测试不会在 0 秒直接判负：问题先记录，不稳定部分先垮塌，车辆照常行驶；遇到断口卡住 3 秒判负；驶上断裂桥面才坠落。局部构件掉落不算致命。
- 预算可以超支，只影响评分。
- 评分上不封顶：预算 : 完整度 : 受力 = 7 : 2 : 1。预算分 = 7000 × 预算 / 花费；完整度分 = 2000 × 桥面完整度；受力分 = 1000 × max(最大利用率, 安全冗余)，安全冗余 = 1/利用率 − 1，上限 150%。用户确认“利用率越高越好”。
- 每种桥面在自身最大长度内（两端支撑）必须能承载规定车辆：木桥面要求轿车，其余桥面要求重型货车（有回归测试）。
- 坡度：建议 ≤ 15%（容差 0.5 个百分点）；15%~30% 车辆减速爬坡；> 30% 爬不上去。
- 立柱有倾角限制（混凝土柱 15°、钢筋混凝土柱 30°、桥墩 5°），超限可建但测试开始时倒塌。
- 桥墩工具：从河床立起，自动生成墩底基础 + 中部 + 顶部支点；基础可选浅基础 / 桩基础；软土浅基础承载低；洪水冲毁木桥墩、掏空冲刷区浅基础。
- 旧桥加固：旧构件不计费、节点不能移动、最多拆 4 根、可付费修复。
- 界面：建造默认正交侧视；建造与测试底栏分开；失败弹窗先给一句话原因和建议，主按钮“定位问题并返回建造”。

## 关卡（12 关，按难度递增）
1 跨越小河 · 2 林间步道（胶合木限量）· 3 重型货车 · 4 高低两岸 · 5 有限锚点 · 6 坦克通行 · 7 航道净空（禁建墩 + 船）· 8 货运铁路 · 9 城市立交（双层）· 10 旧桥加固 · 11 软土与桩基（车队）· 12 洪水冲刷。

## 尚未实现（用户提过，留待以后）
钢—混凝土组合梁、弧形胶合木梁、温度伸缩、加固施工顺序（分段封闭）、双车道偏载 / 侧风 / 桥面扭转（需要横向受力计算）、液压开合桥（测试中可控）、自由建造模式。

## 工作约定
- 改动后：跑测试 → 编译游戏 → 更新 README 相关章节。
- Git：仓库 https://github.com/RonganBai/Bridge-Builder-Simulator ，分支 main；提交说明用中文或英文均可，结尾加 Co-Authored-By 行。推送前先征得用户同意。
- 没在 Godot 里实际运行过的改动，要明确告诉用户“未在 Godot 中验证”。
