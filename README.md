# SprocketTools

Sprocket 载具设计器的编辑工具集，中文界面，独立维护。当前版本 1.0.2，运行于 BepInEx 6（IL2CPP 版），
验证环境为 Sprocket 0.2.55.5 + Unity 6000.3.21 + `6.0.0-be.788`。

本仓库是一条独立维护的支线，代码源自上游 [Quality of Life](https://github.com/Hans21223/Sprocket-Quality-of-Life)
（作者 Hans21223，MIT 许可），挂点架构、界面层与多项交互已经重做，功能取舍与发布节奏由本仓库决定；
它不是上游的翻译版本，也不与上游同步。许可与署名见文末。

## 功能

功能按类装载：`Plugin.cs` 中的 `features` 数组对每个功能类调用一次 `Harmony.PatchAll`。
若某个类挂接失败（例如游戏版本变动导致目标方法不存在），日志输出一行
`<类名> disabled, could not attach to the game`，该类功能不可用，其余功能不受影响。

启动日志中固定输出的功能行如下：

```
Turret to Add-on, Merge add-ons, Cut with add-on, Hole quality, Merge faces, Mesh tools,
Smooth edge, Split faces, Hotkeys, Turret copy, Exploded view, Gun length,
Speed & acceleration, Max-quality photo, Photo card, Own paint,
Bridge, Circle, Fix mirror, Mirror merge, Rotation snap
```

功能入口以可折叠小节的形式出现在游戏原生的检查器面板中，折叠状态记忆在配置文件里。
编辑器内按 F1 显示快捷键总表。图纸（F9）与资料卡（F10）的输出参数在配置文件中调整。

## 实现方式

- **改动全部通过 Harmony 前后挂实现**，运行期由单个 `DesignEditor` 组件承担每帧驱动，
  不生成、不替换游戏程序集。
- **界面文字为源码内嵌中文**，不依赖任何汉化插件的词条替换。
- **面板沿用游戏原生检查器布局**，不自建窗口层；少数浮层（快捷键框、尺寸标注、状态条）走 IMGUI，
  并在首次绘制前把字体指向带中文字形的系统字体。
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

产物：`bin\Release\net6.0\SprocketTools.dll`。

## 部署与配置

- 将 DLL 放入 `<游戏根目录>\BepInEx\plugins\`。
- 配置文件：`BepInEx\config\local.sprocket.tools.cfg`，首次启动按默认值生成。
- 设计备份：`BepInEx\SprocketToolsBackups\`，保留数量由 `Backups kept` 控制。
- interop 程序集按游戏版本生成，因此 DLL 只在与构建时相同的游戏版本与框架版本上运行稳定。

## 配套插件：部件剪贴板

部件剪贴板（设计器里 Ctrl+C 复制／Ctrl+X 剪切／Ctrl+V 粘贴，跨图纸可用）是独立仓库独立 DLL，
源码与行为规则见 [Sprocket-Clipboard](https://github.com/Sevenight88/Sprocket-Clipboard)。
它依赖本 mod，没装本 mod 时 BepInEx 不会装载它；装上后 F1 快捷键表多出一行标 `[剪贴板]` 的按键说明，其余界面无变化。
两个仓库放在同级目录时，剪贴板的 csproj 会自动找到本工程的引用程序集与产物，构建顺序是先本仓库、再那边。

## 仓库文件

源码平铺在根目录，一个文件一个职责；下表按功能归类，逐条说明各文件承担什么。

**入口与界面层**

| 文件 | 作用 |
| --- | --- |
| `Plugin.cs` | BepInEx 入口：注册全部配置项、逐个功能类调 `Harmony.PatchAll`、输出装载日志；文件内的 `Ui` 静态类提供可折叠小节、异常护栏与 IMGUI 中文字体接线；文件头对 `SprocketClipboard` 开放 `internal` |
| `Editing.cs` | 编辑驱动核心：`DesignEditor` 组件负责每帧驱动，把设计存成 JSON、修改、备份两份、再交游戏重载；末尾的 `RestoreSection` 是面板中的「撤销上一次修改」小节；另负责镜像塔与复制塔留下的空炮塔环——游戏自己的放置一结束就把塔补到对应体上（每个对应体只补一次） |
| `PanelFit.cs` | 检查器行的尺寸自适应：向 TextMeshPro 询问中文在当前字体下的实测宽高，避免标签被裁、多行说明被下一行压住 |
| `Hotkeys.cs` | F1 快捷键浮窗（按键取自游戏实时绑定，改过键位就显示改后的），另实现移动/缩放时的 Shift+轴锁定；`Extra` 是留给剪贴板登记按键行的口子 |

**附加部件与结构编辑**

| 文件 | 作用 |
| --- | --- |
| `TurretToAddon.cs` | 炮塔环与车体结构面板中的「炮塔转附加部件」小节（类名 `InspectorSection`） |
| `Conversion.cs` | 炮塔转附加部件的蓝图改写：炮、乘员与挂载部件留在原位 |
| `ShapeTools.cs` | 附加部件面板的两个入口：「合并附加部件」（Ctrl+J）与「用此附加部件切割」 |
| `AddonEdits.cs` | 生成、合并附加部件的蓝图编辑，纯 JSON 不碰游戏代码，可离线验证 |
| `TurretDriveRepair.cs` | 「修复镜像炮塔旋转驱动」小节：把接错的驱动重新接到各自挂载的炮塔 |
| `TurretMotorLifecycle.cs` | 炮塔环与马达事件的生命周期收口：游戏原生初始化每次都会再挂一个监听，而释放与销毁都不退订；这里在环销毁、释放、换马达前后补退订，并拦下已经没有马达的环的回调 |
| `MeshCut.cs` | 用闭合形状对板网格做布尔切割（通孔／带侧壁凹槽），并把切出的碎片并回四边面 |
| `Fill.cs` | 平面填充内核：贴孔一圈四边面逐圈内收、多边形分环收向中心、其余走 Delaunay，最后把三角形配成凸四边面 |
| `MergeFaces.cs` | 「合并所选面」小节，作为游戏自带的一次网格编辑运行，因此 Ctrl+Z 可撤销 |
| `FaceMerge.cs` | 上述合并的纯数学部分：按共享边合并、在弯折处拆分、按选项处理被未选面共用的边点 |
| `HoleQuality.cs` | 「圆孔质量」小节：给游戏的创建孔工具加边数、尺寸与填充方式选项 |
| `HoleRing.cs` | 孔环的几何计算：把游戏生成的孔环修正为位于面内、朝向与面一致的真圆，纯计算可离线测试 |
| `MirrorMerge.cs` | 两个小挂点：`RotationSnap`（自定义旋转步进）与镜像开启时把另一侧镜像点一并合并 |

**网格工具**

| 文件 | 作用 |
| --- | --- |
| `MeshTools.cs` | 工具的执行侧与视图侧：展平／循环切割／内插面／倒角／圆角／圆滑边／分割面／选择同向相连面／比例编辑／桥接／圆化／校正镜像，外加正交视图、0.5 毫米吸附与 `TurretCopy`（Alt 复制炮塔）；面板里还带出「部件质心标记」开关；仓库中最大的一个文件 |
| `MeshPlans.cs` | 上述工具的计划层：只算点索引，不碰游戏类型，因此可离线测试 |
| `BevelEdges.cs` | 倒角时把边设置带过被缩短的侧边，结果不依赖面的生成顺序 |
| `EdgeFillet.cs` | 圆角的计划层：按真实圆弧半径（毫米）与分段数生成，半径非法、边两侧不是恰好两个面或交接缝倒不出来时原样返回输入，不动网格 |
| `EdgeSubdivision.cs` | 直线切割向对边传播：交叉切割形成网格，而不是扇形三角 |
| `SplitFaceSelection.cs` | 「选中分割之间的面」：改用几何比较，不保留撤销/重做后会失效的 Face 指针 |
| `GizmoPicking.cs` | 放开游戏 100 米的圆环拾取上限，正交相机拉远后仍能点中操作圆环 |

**出图**

| 文件 | 作用 |
| --- | --- |
| `DrawingSheet.cs` | F9 工程图纸：四视图同比例排布、1 米标尺、标题栏文字，输出线稿／彩色／透视三张 PNG |
| `Drawing.cs` | 图纸中与游戏无关的计算部分：边线判定（面夹角、开边、轮廓）、逐视图可见性、成图写入 |
| `DrawingMounts.cs` | 把炮归属到最近的耳轴，而不是沿祖先链一路挂到的每个枢轴 |
| `DrawingOptions.cs` | 图纸的纯绘制开关：不改动活动火炮、涂装或载具 |
| `DrawingSettings.cs` | 「工程图纸（F9）」小节：在板结构、炮塔环、炮三个面板后挂出图纸与火炮活动选项 |
| `GunAnnotationPreferences.cs` | 「该炮极限标注」的导出偏好，按设计名加炮的 VUID 记录，不写进设计本身 |
| `Card.cs` | F10 资料卡：按参考图定下的机位与打光输出白底高清图，尺寸标注与图层剔除沿用图纸那套已验证路径 |
| `PhotoShot.cs` | F8：不退出拍照模式按最高画质出片，拍完还原画质与界面设置 |

**信息与观察**

| 文件 | 作用 |
| --- | --- |
| `GunLength.cs` | 炮面板里的火炮长度：以口径倍数 L/xx 给出身管加药室的全长 |
| `GearSpeeds.cs` | 变速箱与发动机面板里的「速度与加速」：各挡极速、可用功率与零到极速时间 |
| `Acceleration.cs` | 上述数据的算法来源：按游戏自身传动规则逐步模拟（离线验证，不调用游戏代码） |
| `ExplodedView.cs` | F2 爆炸视图：部件按挂载关系移开，只影响画面，存读设计前会自动归位 |
| `PartPaint.cs` | 部件自有涂装：占用游戏闲置的 User 1–9 涂装槽，使用关系写在该涂装的描述里随设计保存；`ImageAddresses` 负责迷彩图的地址解析 |
| `PartPaintPersistence.cs` | 自有涂装存盘数组的边界处理：原生构造器的默认数组长度同时决定读几个存档槽位，占用槽位超出时补齐占位再交原生填充，避免整段涂装读取被拒绝 |
| `MassMarkers.cs` | 部件质心标记：游戏里视图筛选的 COM 主开关与逐部件的质心抽屉是两个各自独立的原生开关，这里在主开关之下单独控制后者，原生抽屉缓存的重建路径也一并接住 |

**工程与许可**

| 文件 | 作用 |
| --- | --- |
| `SprocketTools.csproj` | 构建脚本：引用 `libs\` 的 4 个框架程序集与 `GameDir` 指向的游戏 `BepInEx\interop\`；未提供游戏目录时直接报错说明缺什么 |
| `LICENSE` | MIT 声明，含上游与本支线两段版权 |
| `LICENSE.upstream.txt` | 上游 Quality of Life 的 MIT 许可原文 |
| `libs/README.md` | 构建所需 4 个框架 DLL 的来源与放置位置；DLL 本身不入库 |
| `.gitignore` / `.gitattributes` | 排除 `bin/`、`obj/` 与第三方二进制；统一文本行尾 |

界面文字的中文措辞与术语由仓库维护者另行掌握，改动界面文字时注意：日志行与 `TOOL_*`、`CARD_*` 前缀、
配置节名与键名、参与比较或查找的字符串、GameObject／灯光／材质名必须保持英文。

## 许可与署名

MIT。本仓库的修改与新增部分 © 2026 Sevenight，声明见 `LICENSE`；
上游 `Quality of Life` © 2026 Hans21223 的 MIT 许可原文见 `LICENSE.upstream.txt`。再分发时请一并保留这两份文件。
