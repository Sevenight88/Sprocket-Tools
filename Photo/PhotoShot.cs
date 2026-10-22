using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.Photomode;
using Sprocket.SettingConfiguration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// 拍照模式按 F8：不离开拍照模式，用最高画质拍一张。设置临时拉高，拍照模式下遮罩收起，拍完都恢复。
/// 存到 Documents\My Games\Sprocket\Photos。可选屏幕/2K/4K/6K/8K，以及渲染（高分辨率重画）或放大（放大截图）。
internal static class PhotoShot
{
    const int SettleFrames = 45; // 阴影、纹理、屏幕空间效果追上新高画质
    static int step = -1, frames;
    static PhotomodeOverlay? overlay;
    static SprocketApplication? app;
    static SettingsProfile? before;
    static bool overlayWas, overlayCaptured;
    static int captureId; // 迟到的帧不能覆盖更新一张的文件
    static string file = "";
    static string resolution = "屏幕", method = "渲染";
    static RenderPhoto? activeFrame;

    /// 为 true 时 mod 什么都不画（否则会进照片）。
    internal static bool Capturing => step >= 0;

    internal static void Update()
    {
        try
        {
            if (step >= 0) { Advance(); return; }
            if (DrawingSheet.Capturing || Card.Capturing || !Keybinds.Pressed("photo") || MeshTools.Typing()) return;
            overlay = UnityEngine.Object.FindObjectOfType<PhotomodeOverlay>();
            app = UnityEngine.Object.FindObjectOfType<SprocketApplication>();
            if (overlay == null || app == null) return; // 不在拍照模式
            resolution = Plugin.PhotoResolution?.Value ?? "屏幕";
            method = Plugin.PhotoMethod?.Value ?? "渲染";
            var size = PhotoOutput.SizeFor(Screen.width, Screen.height, resolution);
            if (resolution != "屏幕" && method == "渲染" && !PhotoOutput.FitsRenderTarget(size, SystemInfo.maxTextureSize))
                throw new InvalidOperationException("显卡不能渲染这个照片尺寸。请选更小的分辨率，或在「Sprocket工具包 → 照片」里改用「放大」。");
            overlayWas = overlay.overlayVisible;
            overlayCaptured = true;
            captureId++; // 之前还挂着的帧现在都太迟了
            var current = app.CurrentSettings;
            before = Profile(current, current.Graphics.Copy());
            var max = Profile(current, current.Graphics.Copy());
            Maximise(max.Graphics);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Photos");
            Directory.CreateDirectory(dir);
            file = Drawing.UnusedPath(Path.Combine(dir, $"Sprocket {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png"));
            overlay.SetOverlayVisible(false);
            app.ApplySettings(max);
            step = 0; frames = 0;
            Plugin.ModLog.LogInfo($"TOOL_PHOTO {resolution}，{method}；最高画质已应用，{SettleFrames} 帧后拍");
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"TOOL_PHOTO 无法开始：{ex}");
            Restore();
        }
    }

    static void Advance()
    {
        try
        {
            if (overlay == null || app == null || DesignEditor.Instance == null) { Restore(); return; }
            frames++;
            if (step == 0 && frames >= SettleFrames)
            {
                shotDone = false; shotError = null;
                if (DesignEditor.Instance is { } editor) editor.StartCoroutine(Shoot(captureId, file).WrapToIl2Cpp());
                else throw new InvalidOperationException("没有可运行拍照协程的编辑器");
                step = 1; frames = 0;
            }
            else if (step == 1 && (shotDone || frames > 120))
            {
                Restore();
                if (shotDone && shotError == null)
                {
                    long kb = File.Exists(file) ? new FileInfo(file).Length / 1024 : 0;
                    Plugin.ModLog.LogInfo($"TOOL_PHOTO 已保存 {file}（{kb} KB）");
                    DesignEditor.Instance?.Say("照片已保存：" + file, 6);
                }
                else
                {
                    Plugin.ModLog.LogError($"TOOL_PHOTO 未保存：{shotError?.ToString() ?? "画面始终没来"}");
                    DesignEditor.Instance?.Say("照片未保存：" + (shotError?.Message ?? "画面始终没就绪"), 6);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"TOOL_PHOTO 失败：{ex}");
            Restore();
        }
    }

    static bool shotDone;
    static Exception? shotError;

    /// 屏幕上完成的一帧存成 PNG。游戏自己的 ScreenCapture.CaptureScreenshot 不能在 mod 里用：文件名不经过它。
    static System.Collections.IEnumerator Shoot(int id, string destination)
    {
        yield return new WaitForEndOfFrame(); // 等所有东西画完
        // 超时或已恢复的协程不能拍恢复后的设置、覆盖后一张的文件，也不能替新一次拍报告完成。
        if (id != captureId || step < 0) yield break;
        Texture2D? shot = null;
        RenderPhoto? frame = null;
        try
        {
            PhotoOutput.Size size = default;
            try
            {
                size = PhotoOutput.SizeFor(Screen.width, Screen.height, resolution);
                if (resolution != "屏幕" && method == "渲染")
                    activeFrame = frame = new RenderPhoto(Camera.main ?? throw new InvalidOperationException("没有可用的拍照相机。"), size);
            }
            catch (Exception ex) { if (id == captureId) shotError = ex; }
            if (frame != null) yield return new WaitForEndOfFrame(); // HDRP 下一帧才把离屏相机画完
            if (id != captureId || step < 0) yield break;
            try
            {
                if (shotError != null) throw shotError;
                shot = frame != null ? frame.Read() : ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null) throw new InvalidOperationException("取不到照片画面。");
                var pixels = shot.GetPixels32();
                var rgb = new byte[checked(pixels.Length * 3)];
                int nonOpaque = 0;
                for (int p = 0; p < pixels.Length; p++)
                {
                    var pixel = pixels[p];
                    rgb[p * 3] = pixel.r; rgb[p * 3 + 1] = pixel.g; rgb[p * 3 + 2] = pixel.b;
                    if (pixel.a != 255) nonOpaque++;
                }
                // HDRP 最终 RGB 已有烟尘等效果；alpha 里可能还留着粒子覆盖/扰动值。
                // 再编码这个 alpha，看图软件会把烟尘又合成一遍。这里只存不透明 RGB，不再乘/合成颜色。
                var outputSize = resolution == "屏幕" ? new PhotoOutput.Size(shot.width, shot.height) : size;
                rgb = PhotoOutput.ResizeRgb(rgb, shot.width, shot.height, outputSize);
                Drawing.SavePng(destination, outputSize.Width, outputSize.Height, rgb);
                Plugin.ModLog.LogInfo($"TOOL_PHOTO {outputSize.Width}x{outputSize.Height}，{method}，不透明 RGB；丢弃了 {nonOpaque} 个像素的渲染 alpha");
            }
            catch (Exception ex) { if (id == captureId) shotError = ex; }
        }
        finally
        {
            if (shot != null) UnityEngine.Object.Destroy(shot);
            frame?.Dispose();
            if (ReferenceEquals(activeFrame, frame)) activeFrame = null;
        }
        if (id == captureId) shotDone = true;
    }

    sealed class RenderPhoto : IDisposable
    {
        Camera? camera;
        RenderTexture? target;
        RenderTexture? previousTarget;
        Rect previousRect;
        float previousAspect;
        bool previousDynamicResolution;
        bool ownsCameraState;
        readonly PhotoOutput.Size size;
        internal RenderPhoto(Camera source, PhotoOutput.Size size)
        {
            this.size = size;
            try
            {
                target = new RenderTexture(size.Width, size.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                if (!target.Create()) throw new InvalidOperationException("分配高分辨率照片失败。请选更小的尺寸，或改用「放大」。");
                // 用真正的拍照相机，原生载具/履带渲染和相机专属效果才正常；只改它一帧的输出目标，不动游戏窗口。
                camera = source;
                previousTarget = source.targetTexture; previousRect = source.rect; previousAspect = source.aspect;
                previousDynamicResolution = source.allowDynamicResolution; ownsCameraState = true;
                camera.targetTexture = target;
                camera.rect = new Rect(0, 0, 1, 1); camera.aspect = size.Width / (float)size.Height;
                camera.allowDynamicResolution = false;
            }
            catch { Dispose(); throw; }
        }
        internal Texture2D Read()
        {
            if (target == null) throw new InvalidOperationException("照片渲染已取消。");
            if (camera == null || camera.targetTexture != target) throw new InvalidOperationException("拍照相机在画面就绪前变了。");
            Texture2D? image = null;
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                image = new Texture2D(size.Width, size.Height, TextureFormat.RGBA32, 1, false);
                image.ReadPixelsImpl(new Rect(0, 0, size.Width, size.Height), 0, 0, false);
                return image;
            }
            catch { if (image != null) UnityEngine.Object.Destroy(image); throw; }
            finally { RenderTexture.active = previous; }
        }
        public void Dispose()
        {
            if (camera != null && ownsCameraState)
            {
                // 若别的工具中途接管了相机，就不要动它的目标和设置。
                if (camera.targetTexture == target)
                {
                    camera.targetTexture = previousTarget; camera.rect = previousRect; camera.aspect = previousAspect;
                    camera.allowDynamicResolution = previousDynamicResolution;
                }
            }
            ownsCameraState = false;
            camera = null;
            if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            target = null;
        }
    }

    /// 设置恢复原样，遮罩若原来显示就恢复显示。
    static void Restore()
    {
        step = -1;
        captureId++; // 还在路上的帧属于到此结束的这次尝试
        try { activeFrame?.Dispose(); }
        catch (Exception ex) { Plugin.ModLog.LogError($"TOOL_PHOTO 释放渲染目标失败：{ex}"); }
        activeFrame = null;
        try { if (app != null && before != null) app.ApplySettings(before); }
        catch (Exception ex) { Plugin.ModLog.LogError($"TOOL_PHOTO 恢复画质设置失败（重开设置页可修复）：{ex}"); }
        try { if (overlay != null && overlayCaptured) overlay.SetOverlayVisible(overlayWas); } catch { }
        before = null; app = null; overlay = null; overlayCaptured = false;
    }

    /// 玩家设置的副本，画质设置独立（副本的可能和原件共用）。
    static SettingsProfile Profile(SettingsProfile source, GraphicsSettings graphics)
    {
        var copy = new SettingsProfile(source);
        copy.graphics = graphics;
        return copy;
    }

    /// 每个画质项拉到最高。观感（暗角、颗粒、模糊、景深开关）、分辨率和抗锯齿类型保持玩家原样。
    static void Maximise(GraphicsSettings g)
    {
        g.TextureResolution = GraphicsSettings.TextureResolutionMode.High;
        g.ShadowDistanceMode = GraphicsSettings.ShadowDistanceModeType.VeryFar;
        g.ShadowQuality = GraphicsSettings.ShadowQualityMode.Ultra;
        g.AmbientOcclusionMode = GraphicsSettings.AmbientOcclusionModeType.High;
        g.ContactShadowsMode = GraphicsSettings.ContactShadowModeType.High;
        g.ContactShadowsEnabled = true;
        g.ScreenSpaceGlobalIlluminationMode = GraphicsSettings.ScreenSpaceGlobalIlluminationModeType.High;
        g.ScreenSpaceReflectionMode = GraphicsSettings.ScreenSpaceReflectionModeType.High;
        g.ScreenSpaceReflections = true;
        g.MicroShadows = true;
        g.DistantObjectDetail = GraphicsSettings.DistantObjectDetailMode.High;
        g.GrassDistanceMode = GraphicsSettings.GrassDistanceModeType.Far;
        g.GroundDetailDensityMode = GraphicsSettings.GroundDetailDensityModeType.High;
        g.DepthOfField = GraphicsSettings.DepthOfFieldQualityMode.High;
        g.AnisotropicFiltering = AnisotropicFiltering.ForceEnable;
        g.AntiAliasingQuality = 2;
        g.DynamicResolutionMode = DynamicResolutionMode.Off; // 全分辨率，不放大
        g.DLSS = false;
        // 不知道最高值的项：用游戏「最高」预设，除非玩家本来更高（预设越高质量数值越小，地形和着色器反之）。
        var presets = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GraphicsSettingsContainer>())
            .Select(o => o.TryCast<GraphicsSettingsContainer>()).Where(p => p?.Settings != null).ToList();
        if (presets.FirstOrDefault(p => p!.Name == "Maximum")?.Settings is not { } best) return;
        g.QualityLevel = Math.Min(g.QualityLevel, best.QualityLevel);
        g.ShaderQualityLevel = Math.Max(g.ShaderQualityLevel, best.ShaderQualityLevel);
        g.TerrainQualityLevel = Math.Max(g.TerrainQualityLevel, best.TerrainQualityLevel);
        Plugin.ModLog.LogInfo($"TOOL_PHOTO 预设 {string.Join(", ", presets.Select(p => $"{p!.Name}（质量 {p.Settings.QualityLevel}，着色 {p.Settings.ShaderQualityLevel}，地形 {p.Settings.TerrainQualityLevel}）"))}");
    }
}
