# libs/ 放什么

构建需要 4 个引用程序集。它们是第三方框架二进制，不入库，从你手上的 BepInEx 6 里拷过来：

```
libs/0Harmony.dll
libs/BepInEx.Core.dll
libs/BepInEx.Unity.IL2CPP.dll
libs/Il2CppInterop.Runtime.dll
```

这 4 个文件都在 **BepInEx 6（IL2CPP 版）** 解压后的 `BepInEx\core\` 目录里，
本仓库按 `6.0.0-be.788` 编译。

## 还差一样：游戏的 interop 程序集

工具集要操作 Sprocket 自己的类型（`VehicleDesignerCore`、`DesignItem` 等），
所以 csproj 里额外引用了 `$(GameDir)\BepInEx\interop\*.dll`。这些 DLL 由 BepInEx
在**首次启动游戏时**根据当前游戏版本生成，不能预先发给你。

准备顺序：

1. 给 Sprocket 装 BepInEx 6（IL2CPP 版，含 Doorstop 引导）。
2. 启动一次游戏，等主菜单出来（这一步会生成 `BepInEx\interop\`，首次可能要一两分钟）。
3. 构建时用 `-p:GameDir` 指到游戏根目录：

```
dotnet build SprocketTools.csproj -c Release -p:GameDir="D:\Steam\steamapps\common\Sprocket"
```

也可以设环境变量 `SPROCKET_DIR`，效果相同。没设置时构建会直接报错并告诉你缺什么，
不会报一堆看不懂的 CS0246。

**注意**：interop 程序集按游戏版本生成，因此使用某个版本的 `interop\` 编出的 DLL，
只在与构建时相同的游戏版本与框架版本上运行稳定。本项目在 Sprocket 0.2.55.5 + `6.0.0-be.788` 上验证。
