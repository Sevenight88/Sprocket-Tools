# SprocketTools

Sprocket 载具设计器的编辑工具集，中文界面，独立维护。当前版本 1.3.0，运行于 BepInEx 6（IL2CPP 版），
验证环境为 Sprocket 0.2.56.1 + Unity 6000.3.21 + `6.0.0-be.789`。

本仓库是一条独立维护的支线，代码平行于上游 [Quality of Life](https://github.com/Hans21223/Sprocket-Quality-of-Life)，挂点架构、界面层与多项交互已经重做，功能取舍与发布节奏由本仓库决定；
不与上游同步。许可与署名见文末。

## 功能

功能按类装载：`Plugin.cs` 中的 `features` 数组列出全部功能类，每一类再由 `Hooks.Attach` **一条挂点一条挂点**地接
（`Hooks.cs`）。游戏更新改掉或删掉某个方法时，只有用到它的那一条挂点失效，同一个小节的其他挂点照旧，
日志与「游戏设置 → Sprocket工具包 → 兼容性」会指名是哪一条。装载完成的日志行为：

```
TOOL_HOOKS <接上的条数>／<失败的条数> …
SprocketTools 1.3.0 loaded（Ctrl+K 打开 Mod Options）
```

另有 `[timing]` 一行给出总耗时、最慢的五个功能类，以及推迟到首次进设计器才接的挂点条数。
发动机面板的类在 0.2.56 换了程序集，编译期写死只认一个版本，因此那两条挂点是运行时按类型名找到再手工接的。

除原有的结构编辑、网格工具与出图三块，1.3.0 还带这些：

- **信息与观察**：挡位间距与变速箱「平滑」重算、火炮垂直稳定器与双向稳定器（**实验性**，只发瞄准指令不改游戏数据）、
  七类自造装甲材质接进游戏自己的科技表、观察窗玻璃改涂灰、车灯开关（L）、爆反起爆特效（程序生成，不依赖美术资产）、
  连发炮的炮口声源池上限、放开设计器里 Penetration / Caliber 的上限。
- **命中读数**：只读地取游戏自己算完的那次穿透模拟（官方认出的部件与原弹头在板内的行程），
  以及配套的弹六型 × 甲七类参数表；两者都不改写游戏数据。
- **模型进出**：OBJ 导出 / 导入菜单（Ctrl+O）。导出读原生渲染几何，不动部件；导入把 OBJ 转成游戏的板结构格式，
  再把按 UV 与法线缝拆开的点焊回可编辑的少边数板面。另有 Blender 式的布料垂坠。
- **界面与按键**：按键集中在 `Keybinds.cs`，默认 Ctrl+K 打开按键与设置窗口（任何界面都能开），
  游戏自带的设置界面里另有一页「Sprocket工具包」（工具按键／编辑按键／设置／兼容性四个分页），改的是同一批键；
  配置存在 `[Keybinds]` 一节。鼠标停在自有面板或浮层上时，游戏自己的点击、拖动与滚轮暂时关掉（`InputShield.cs`）；
  Ctrl+右键（不拖动）在编辑器里打开本工具集的动作菜单（`RightClickMenu.cs`）。

功能入口以可折叠小节的形式出现在游戏原生的检查器面板中，折叠状态记忆在配置文件里。
编辑器内按 F1 显示快捷键总表。图纸（F9）与资料卡（F10）的输出参数在配置文件中调整。

## 实现方式

- **改动全部通过 Harmony 前后挂实现**，逐条挂点各带失败上报（见上一节），运行期由单个 `DesignEditor` 组件承担每帧驱动，
  不生成、不替换游戏程序集。
- **例外只有一处**：游戏设置里的那一页必须是游戏自己的一个设置页类型，所以 `ModOptions.cs` 用
  `ClassInjector.DerivedConstructorPointer` 注册了一个 `SettingsSubMenu` 的子类（全仓库唯一一处这种注册）。
  其余功能都不往游戏运行时派生新类型。
- **界面文字为源码内嵌中文**，不依赖任何汉化插件的词条替换。
- **七份装甲材质定义以嵌入资源随 DLL 走**（`Materials\*.json`），首次运行时落地到 `BepInEx\plugins\SprocketMaterials\`，
  再交给游戏自己的科技表解析器；材质条目因此跟 DLL 同进退，不需要另发数据目录。
- **装甲材质一节分两半**：本仓库自己画的是板块材质下拉（写 `armourTechID`，走设计的正常修改通道、改前有备份）、
  材质读数、「整车统一」与「清除」；外观材质、面材质与反应装甲面板全部经反射取另一个 mod（装甲黎明）的类型来画，
  因此「装甲材质」在面板里只有一个入口，且这里不产生对那个程序集的编译依赖。装甲黎明不在场时那几处反射拿不到类型，
  对应小节自己消失，材质下拉与读写不受影响；关掉 `Enable custom materials` 则整段让位，勾选框回到游戏自己画。
- **面板沿用游戏原生检查器布局**，检查器里的小节与快捷键框、尺寸标注、状态条走 IMGUI，
  并在首次绘制前把字体指向带中文字形的系统字体；需要整块窗口（按键与设置、OBJ 菜单）时才用游戏自己的 uGUI 与字体
  建一层全屏画布（`NativeUi.cs`），不另起一套界面体系。
- 按键是配置文件里的键名，由 `Keybinds.cs` 查表（底层仍是 Unity Input System）：改键只改配置，不需要重编译；
  一个键只能给一个功能，把某个键改给新功能时会先从旧功能上摘掉。
- 展平热键为 **N**：P 在游戏内已被拍照模式占用。
- 图纸中的天线**仅按几何特征判定并排除**，不依赖部件的组件标记；组件标记会漏掉轨式与框式天线。
- 图纸的遮罩由亮品红键那一趟判定，**彩色图与透视图的颜色另取自纯黑背景的一趟**：品红键会往轮廓一到两个像素里晕出一条同色边，
  黑底供色直接去掉这条边，车身深处两趟颜色一致。
- 部件剪贴板（Ctrl+C 复制／Ctrl+X 剪切／Ctrl+V 粘贴，可跨图纸）**不编进本程序集**，是另一个仓库另一个 DLL：
  它直接改写蓝图的部件表与引用，风险量级与这里的界面级改动不同，单独成包便于按需要装或撤。
  它复用本仓库的 `Conversion.cs`（蓝图 id 规则）与 `DesignEditor.RequestEdit`，因此声明了对 `local.sprocket.tools`
  的依赖；本仓库为它留的接口只有一处：F1 快捷键表的 `Hotkeys.Extra`。详见 [Sprocket-Clipboard](https://github.com/Sevenight88/Sprocket-Clipboard)。
- 日志行（前缀 `TOOL_*`、`CARD_*`）、配置节名与键名**保持英文**，便于跨版本比对与问题排查。

## 构建

前置条件：.NET 6 SDK；一份可启动的 Sprocket（已装 BepInEx 6 IL2CPP 版并至少启动过一次，以生成
`BepInEx\interop\`）；以及 `libs\` 中的 4 个框架 DLL。目录与版本要求见 `libs/README.md`。

```
dotnet build SprocketTools.csproj -c Release -p:GameDir="D:\Steam\steamapps\common\Sprocket"
```

`GameDir` 也可通过环境变量 `SPROCKET_DIR` 提供。未设置时构建会直接报错说明缺失项。

产物：`bin\Release\net6.0\SprocketTools.dll`。`Materials\` 的七份 json 由 csproj 作为嵌入资源打进 DLL，
因此构建与部署都只有一个文件要搬。

## 部署与配置

- 将 DLL 放入 `<游戏根目录>\BepInEx\plugins\`。
- 配置文件：`BepInEx\config\local.sprocket.tools.cfg`，首次启动按默认值生成。
- 自造装甲材质的定义文件在首次启动时从 DLL 落地到 `BepInEx\plugins\SprocketMaterials\`；删掉该目录会在下次启动重新生成。
- 设计备份：`BepInEx\SprocketToolsBackups\`，保留数量由 `Backups kept` 控制。
- interop 程序集按游戏版本生成，因此 DLL 只在与构建时相同的游戏版本与框架版本上运行稳定。

## 配套插件：部件剪贴板

部件剪贴板（设计器里 Ctrl+C 复制／Ctrl+X 剪切／Ctrl+V 粘贴，跨图纸可用）是独立仓库独立 DLL，
源码与行为规则见 [Sprocket-Clipboard](https://github.com/Sevenight88/Sprocket-Clipboard)。
它依赖本 mod，没装本 mod 时 BepInEx 不会装载它；装上后 F1 快捷键表多出一行标 `[剪贴板]` 的按键说明，其余界面无变化。
两个仓库放在同级目录时，剪贴板的 csproj 会自动找到本工程的引用程序集与产物，构建顺序是先本仓库、再那边。

## 仓库文件

源码按功能分进 8 个文件夹（`Ui` `Mesh` `Parts` `Armor` `Effects` `Photo` `Info` `ModelIo`），一个文件一个职责；
入口三件 `Plugin.cs`／`Hooks.cs`／`Editing.cs` 和 `Materials\`（内嵌资源的资源名前缀依赖它在根）留在根目录。
下表按功能归类，路径相对仓库根，逐条说明各文件承担什么。

**入口与界面层**

| 文件 | 作用 |
| --- | --- |
| `Plugin.cs` | BepInEx 入口：注册全部配置项，把 `features` 数组里的功能类逐个交给 `Hooks.Attach`，输出装载日志与 `[timing]`；文件内的 `Ui` 静态类提供可折叠小节、异常护栏与 IMGUI 中文字体接线；文件头对 `SprocketClipboard` 开放 `internal` |
| `Hooks.cs` | 逐条挂点的装载层：一条一条接、一条一条记，失败的那条连同异常写进报告（日志与「兼容性」分页都读它）；支持攒到首次进设计器再挂 |
| `Ui/Keybinds.cs` | 全部按键的默认表与配置读写（配置文件 `[Keybinds]` 一节，键名写成 `F9`／`Ctrl+K`／`Numpad1`，留空＝不绑键）；面板与快捷键框显示的按键都取自这里 |
| `Ui/ToolsMenu.cs` | Ctrl+K 的按键与设置窗口：一按键一行，点一下再按新键即改（Esc 取消，Backspace 不设键）；打开期间游戏自己的按键是关着的 |
| `Ui/ModOptions.cs` | 游戏自带设置界面里的「Sprocket工具包」一页（工具按键／编辑按键／设置／兼容性）：用游戏自己的控件画，改动即时存档，界面的 Apply／Cancel 不碰它们；本仓库唯一一处向游戏运行时注册派生类型的地方 |
| `Ui/NativeUi.cs` | 浮层与窗口用游戏自己的 uGUI 与字体：全屏遮罩画布、按 1100×760 像素定位的节点、标签与按钮 |
| `Ui/InputShield.cs` | 鼠标停在自有面板或浮层上时，游戏自己的点击、拖动与滚轮暂时关掉，离开后恢复 |
| `Ui/RightClickMenu.cs` | Ctrl+右键（不拖动）在编辑器里打开的动作菜单：只放工具包自己的动作，不改游戏输入、不替游戏按键 |
| `Editing.cs` | 编辑驱动核心：`DesignEditor` 组件负责每帧驱动，把设计存成 JSON、修改、备份两份、再交游戏重载；末尾的 `RestoreSection` 是面板中的「撤销上一次修改」小节；另负责镜像塔与复制塔留下的空炮塔环——游戏自己的放置一结束就把塔补到对应体上（每个对应体只补一次） |
| `Ui/PanelFit.cs` | 检查器行的尺寸自适应：向 TextMeshPro 询问中文在当前字体下的实测宽高，避免标签被裁、多行说明被下一行压住 |
| `Ui/Hotkeys.cs` | F1 快捷键浮窗（按键取自游戏实时绑定，改过键位就显示改后的），另实现移动/缩放时的 Shift+轴锁定；`Extra` 是留给剪贴板登记按键行的口子 |

**附加部件与结构编辑**

| 文件 | 作用 |
| --- | --- |
| `Parts/TurretToAddon.cs` | 炮塔环与车体结构面板中的「炮塔转附加部件」小节（类名 `InspectorSection`） |
| `Parts/Conversion.cs` | 炮塔转附加部件的蓝图改写：炮、乘员与挂载部件留在原位 |
| `Parts/ShapeTools.cs` | 附加部件面板的两个入口：「合并附加部件」（Ctrl+J）与「用此附加部件切割」 |
| `Parts/AddonEdits.cs` | 生成、合并附加部件的蓝图编辑，纯 JSON 不碰游戏代码，可离线验证 |
| `Parts/TurretDriveRepair.cs` | 「修复镜像炮塔旋转驱动」小节：把接错的驱动重新接到各自挂载的炮塔 |
| `Parts/TurretMotorLifecycle.cs` | 炮塔环与马达事件的生命周期收口：游戏原生初始化每次都会再挂一个监听，而释放与销毁都不退订；这里在环销毁、释放、换马达前后补退订，并拦下已经没有马达的环的回调 |
| `Mesh/MeshCut.cs` | 用闭合形状对板网格做布尔切割（通孔／带侧壁凹槽），并把切出的碎片并回四边面 |
| `Mesh/Fill.cs` | 平面填充内核：贴孔一圈四边面逐圈内收、多边形分环收向中心、其余走 Delaunay，最后把三角形配成凸四边面 |
| `Mesh/MergeFaces.cs` | 「合并所选面」小节，作为游戏自带的一次网格编辑运行，因此 Ctrl+Z 可撤销 |
| `Mesh/FaceMerge.cs` | 上述合并的纯数学部分：按共享边合并、在弯折处拆分、按选项处理被未选面共用的边点 |
| `Mesh/HoleQuality.cs` | 「圆孔质量」小节：给游戏的创建孔工具加边数、尺寸与填充方式选项 |
| `Mesh/HoleRing.cs` | 孔环的几何计算：把游戏生成的孔环修正为位于面内、朝向与面一致的真圆，纯计算可离线测试 |
| `Mesh/HoleFill.cs` | 开孔前的整板预检：游戏自己的面检查只看元素之间的连接，塌成一圈的孔环照样能生成「连接都正确」却实心、交叉或出界的面，这里在动手之前把孔周围整块板的计划面查一遍 |
| `Mesh/MirrorMerge.cs` | 两个小挂点：`RotationSnap`（自定义旋转步进）与镜像开启时把另一侧镜像点一并合并 |

**网格工具**

| 文件 | 作用 |
| --- | --- |
| `Mesh/MeshTools.cs` | 工具的执行侧与视图侧：展平／循环切割／内插面／倒角／圆角／圆滑边／分割面／选择同向相连面／比例编辑／桥接／圆化／校正镜像，外加正交视图、0.5 毫米吸附与 `TurretCopy`（Alt 复制炮塔）；面板里还带出「部件质心标记」开关；按行数算本仓库最长的一个文件 |
| `Mesh/MeshPlans.cs` | 上述工具的计划层：只算点索引，不碰游戏类型，因此可离线测试 |
| `Mesh/BevelEdges.cs` | 倒角时把边设置带过被缩短的侧边，结果不依赖面的生成顺序 |
| `Mesh/EdgeFillet.cs` | 圆角的计划层：按真实圆弧半径（毫米）与分段数生成，半径非法、边两侧不是恰好两个面或交接缝倒不出来时原样返回输入，不动网格 |
| `Mesh/EdgeSubdivision.cs` | 直线切割向对边传播：交叉切割形成网格，而不是扇形三角 |
| `Mesh/SplitFaceSelection.cs` | 「选中分割之间的面」：改用几何比较，不保留撤销/重做后会失效的 Face 指针 |
| `Mesh/Cloth.cs` | 布料披盖的纯数学部分：一张平面点阵在重力下落到载具的实心三角形上，靠边簧、面簧与隔点弯簧绷紧（位置基），落点靠摩擦定住、不下穿地面；被 `Parts/AddonEdits.cs` 用来生成披盖形状的附加部件，不碰游戏类型，可离线验证 |
| `Ui/GizmoPicking.cs` | 放开游戏 100 米的圆环拾取上限，正交相机拉远后仍能点中操作圆环 |

**装甲、材质与命中**

| 文件 | 作用 |
| --- | --- |
| `Armor/ArmorMaterials.cs` | 板块的「装甲材质」小节：下拉写 `armourTechID`（走设计的正常修改通道，改前有备份）、材质读数、整车统一与清除；七份自造材质经游戏自己的 `TechTreeLoader` 并进科技表；反应装甲面板与材质条数在同一条挂点上按现有表合并 |
| `Materials\*.json` | 七份自造装甲材质的定义（每份一个 `type`，带齐 `rhaFactor`／`spallFactor`／`density`／`costMultiplier`），作为嵌入资源随 DLL 走，首次运行落地到 `BepInEx\plugins\SprocketMaterials\` |
| `Armor/AmmoArmourRules.cs` | 弹六型 × 甲七类的参数表与裁定（转正角、跳弹临界角、穿透后效、按甲类的挡弹阈值）：本仓库由装甲材质面板取它，把该甲类的规则写进材质读数那一行；表内同时注明每一维到底在游戏的哪一层生效 |
| `Armor/HitSim.cs` | 只读的命中读数：直接取游戏自己算完的那次穿透模拟（官方认出的部件与原弹头在板内的行程），不走 Unity 碰撞体那条实跑给出过 5~7 米离谱距离的路；**本仓库当前没有调用它的挂点**，随源码发布是为了这份读数的可复现与取证 |

**火控、外观与特效**

| 文件 | 作用 |
| --- | --- |
| `Effects/Stabilizer.cs` | 火炮垂直稳定器（单稳）与双向稳定器（双稳），**实验性**：只发瞄准指令，不改任何游戏数据；开关存在火炮自己的 `CannonInstanceBlueprint.Flags` 空位上，随设计存盘、战斗里读 |
| `Effects/StabilizerPanel.cs` | 上述两个勾选框，画在设计器「瞄准驱动」那一格面板里（就是显示已移动质量／俯仰角／方位角／极速的那格） |
| `Effects/GlassLook.cs` | 观察窗玻璃改涂灰：`armourTechID` 只带弹道数字、透明度归板的材质引用，所以「选了玻璃就该透明」从来没被实现过，这里按 `Glass look mode` 走涂灰／透明／关闭三条路 |
| `Effects/VehicleLightSwitch.cs` | 车灯开关（按 L）：官方灯部件只声明了 `model` 组件、没有 `light` 组件，游戏建 `VehicleLights` 模块时拿到的是空 `ILight[]`；这里直接驱动部件模型底下已有的光源 |
| `Effects/EraFx.cs` | 爆反起爆特效：在板的弹孔外侧放一颗会膨胀的发光球加一盏点光闪，尺寸按该板渲染器包围盒最大边长给，默认 0.25 秒收干净；程序生成，不依赖任何美术资产，也不依赖场景里恰好有游戏特效实例 |

**上限与音频护栏**

| 文件 | 作用 |
| --- | --- |
| `Armor/PenetrationLimits.cs` | 放开设计器里 Penetration／Caliber 的逻辑上限与滑条上限（改 BepInEx 配置项，在设计器 `EditorUpdate` 里低频改面板） |
| `Effects/CannonPool.cs` | 连发炮把炮口声源池打爆时只保留最近几条：限制炮口 prefab 的 `MaxInstanceCount`，并在 1 立方米内超过 N 条时停掉最老的 `AudioSource`；不关特效 |

**模型进出（OBJ）**

| 文件 | 作用 |
| --- | --- |
| `ModelIo/ObjTransfer.cs` | Ctrl+O 的 OBJ 导出／导入菜单：模态、用游戏自己的界面控件，遮罩与暂停的设计器动作表保证点击和输入不会顺手改动车体 |
| `ModelIo/ObjTankExport.cs` | 导出侧：读原生渲染几何，不移动、不重建、不改动任何部件 |
| `ModelIo/ObjExportGeometry.cs` | 导出与离线测试共用的几何部分（矩阵用 `System.Numerics` 行向量） |
| `ModelIo/ObjExportPolicy.cs` | 按部件登记表里的组件 id 分类物理部件，不依赖玩家自己起的名字 |
| `ModelIo/ObjMeshFormat.cs` | Wavefront 多边形的读写编解码：右手系、Y 朝上；本编解码器不开任何路径 |
| `ModelIo/ObjFilePicker.cs` | 原生文件对话框：在检查器回调之外调用，自己不持有 Unity 对象或文件 |
| `ModelIo/ObjPlateFiles.cs` | 把导入结果发布为选中且已存在的阵营库里的新板结构；从不覆盖已保存的结构 |
| `ModelIo/ObjBlueprintImport.cs` | 把 OBJ 转成游戏存盘的板结构格式，不碰活动载具也不碰文件；坐标按 OBJ 的米、右手系、Y 朝上，镜像与绕序反向对应导出侧 |
| `ModelIo/ObjWeld.cs` | 让导入的壳重新可编辑：渲染网格到手时已是三角面并在 UV 与法线接缝处断开，这里先焊回同一个点，再把共面相邻面并回游戏格式允许的三／四角板面；纯几何，可离线对着真实板结构量 |

**出图**

| 文件 | 作用 |
| --- | --- |
| `Photo/DrawingSheet.cs` | F9 工程图纸：四视图同比例排布、1 米标尺、标题栏文字，输出线稿／彩色／透视三张 PNG |
| `Photo/Drawing.cs` | 图纸中与游戏无关的计算部分：边线判定（面夹角、开边、轮廓）、逐视图可见性、成图写入 |
| `Photo/DrawingMounts.cs` | 把炮归属到最近的耳轴，而不是沿祖先链一路挂到的每个枢轴 |
| `Photo/DrawingOptions.cs` | 图纸的纯绘制开关：不改动活动火炮、涂装或载具 |
| `Photo/DrawingSettings.cs` | 「工程图纸（F9）」小节：在板结构、炮塔环、炮三个面板后挂出图纸与火炮活动选项 |
| `Photo/GunAnnotationPreferences.cs` | 「该炮极限标注」的导出偏好，按设计名加炮的 VUID 记录，不写进设计本身 |
| `Photo/Card.cs` | F10 资料卡：按参考图定下的机位与打光输出白底高清图，尺寸标注与图层剔除沿用图纸那套已验证路径 |
| `Photo/PhotoShot.cs` | F8：不退出拍照模式按最高画质出片，拍完还原画质与界面设置 |
| `Photo/PhotoOutput.cs` | 出图尺寸与缩放：屏幕／2K／4K／6K／8K 按长边定档并保持源画面比例；「渲染」直接按目标分辨率渲（超出 `maxTextureSize` 就不硬来），「放大」走 RGB 缩放，不再把渲染带的 alpha 合成一次 |

**信息与观察**

| 文件 | 作用 |
| --- | --- |
| `Info/GunLength.cs` | 炮面板里的火炮长度：以口径倍数 L/xx 给出身管加药室的全长 |
| `Info/GearSpeeds.cs` | 变速箱与发动机面板里的「速度与加速」：各挡极速、可用功率与零到极速时间 |
| `Info/DriveSim.cs` | 上述数据的算法来源：按游戏自己的传动规则逐步模拟（发动机、变速箱、起步与分流齿轮、两侧主动轮、履带张力与地面牵引、车体），驾驶员的起步／换挡／补油也照游戏自己做；对着实录行驶数据校过，纯计算不调用游戏代码 |
| `Info/GearSpacing.cs` | 变速箱「平滑」按钮的挡位间距：一挡与最高挡不动，中间各挡放在「让本车最快冲到最高速 90%」的位置；游戏自带的平滑是等比排，这里从等比到向高挡收窄试一遍 |
| `Info/ExplodedView.cs` | F2 爆炸视图：部件按挂载关系移开，只影响画面，存读设计前会自动归位 |
| `Info/PartPaint.cs` | 部件自有涂装：占用游戏闲置的 User 1–9 涂装槽，使用关系写在该涂装的描述里随设计保存；`ImageAddresses` 负责迷彩图的地址解析 |
| `Info/PartPaintPersistence.cs` | 自有涂装存盘数组的边界处理：原生构造器的默认数组长度同时决定读几个存档槽位，占用槽位超出时补齐占位再交原生填充，避免整段涂装读取被拒绝 |
| `Info/MassMarkers.cs` | 部件质心标记：游戏里视图筛选的 COM 主开关与逐部件的质心抽屉是两个各自独立的原生开关，这里在主开关之下单独控制后者，原生抽屉缓存的重建路径也一并接住 |

**工程与许可**

| 文件 | 作用 |
| --- | --- |
| `SprocketTools.csproj` | 构建脚本：引用 `libs\` 的 4 个框架程序集与 `GameDir` 指向的游戏 `BepInEx\interop\`，并把 `Materials\*.json` 作为嵌入资源打进 DLL；未提供游戏目录时直接报错说明缺什么 |
| `LICENSE` | MIT 声明，含上游与本支线两段版权 |
| `LICENSE.upstream.txt` | 上游 Quality of Life 的 MIT 许可原文 |
| `libs/README.md` | 构建所需 4 个框架 DLL 的来源与放置位置；DLL 本身不入库 |
| `.gitignore` / `.gitattributes` | 排除 `bin/`、`obj/` 与第三方二进制；统一文本行尾 |

界面文字的中文措辞与术语由仓库维护者另行掌握，改动界面文字时注意：日志行与 `TOOL_*`、`CARD_*` 前缀、
配置节名与键名、参与比较或查找的字符串、GameObject／灯光／材质名必须保持英文。

## 许可与署名

MIT。本仓库的修改与新增部分
© 2026 Sevenight，声明见 `LICENSE`；
© 2026 Hans21223 的 MIT 许可原文见 `LICENSE.upstream.txt`。再分发时请一并保留这两份文件。

## 致谢

- **Hans21223** —— 上游 `Quality of Life` 的作者，本仓库的挂点架构由此起步。
- **sectumsempra** —— 提供材质编辑工具 `SprocketMaterialEditor`（MelonLoader 版，直接改写蓝图 json 里的
  `armourTechID`）的源码，是本仓库「装甲材质」方向的起点：它先行验证了「换材质只需改这一个字段、
  质量与成本由游戏自己重算」这条结论。本仓库按 BepInEx + 设计编辑通道的写法重新实现，未直接沿用其代码。
- **nytonortaro-lab** —— 分享「Sprocket 0.127 通用模组 · 开发交流包」（`Heat127`／`FireControl127`／`Weld127`
  三套完整 C# 源码与机制文档）。其中的炮弹机制文档（HEAT／APFSDS／ATGM／APDS／APCR 按弹药架保存、弹药预设、
  同一门炮的多弹种性能预览、复合装甲按装甲模块免疫 HEAT/ATGM）是本仓库弹道与装甲方向的参照。
  本仓库在自己的框架（BepInEx 6 IL2CPP + Il2CppInterop）里另写，未使用其源码，弹道与装甲的结论都对着游戏重新核验过。
