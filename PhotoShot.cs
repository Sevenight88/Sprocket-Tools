using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.Photomode;
using Sprocket.SettingConfiguration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketTools;

/// F8 in photo mode: a photo at the highest graphics settings without leaving photo mode. The settings go up and the
/// photo mode overlay hides for the shot, then both come back. Saved to Documents\My Games\Sprocket\Photos.
internal static class PhotoShot
{
    const int SettleFrames = 45; // shadows, textures and screen-space effects catch up with the new settings
    static int step = -1, frames;
    static PhotomodeOverlay? overlay;
    static SprocketApplication? app;
    static SettingsProfile? before;
    static bool overlayWas;
    static string file = "";

    /// While true nothing of the mod draws on screen (it would be in the photo).
    internal static bool Capturing => step >= 0;

    internal static void Update()
    {
        try
        {
            if (step >= 0) { Advance(); return; }
            if (Keyboard.current is not { } keys || !keys.f8Key.wasPressedThisFrame) return;
            overlay = UnityEngine.Object.FindObjectOfType<PhotomodeOverlay>();
            app = UnityEngine.Object.FindObjectOfType<SprocketApplication>();
            if (overlay == null || app == null) return; // not in photo mode
            var current = app.CurrentSettings;
            before = Profile(current, current.Graphics.Copy());
            var max = Profile(current, current.Graphics.Copy());
            Maximise(max.Graphics);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Photos");
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, $"Sprocket {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            overlayWas = overlay.overlayVisible;
            overlay.SetOverlayVisible(false);
            app.ApplySettings(max);
            step = 0; frames = 0;
            Plugin.ModLog.LogInfo($"TOOL_PHOTO max settings applied, taking the photo in {SettleFrames} frames");
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"TOOL_PHOTO couldn't start: {ex}");
            Restore();
        }
    }

    static void Advance()
    {
        try
        {
            frames++;
            if (step == 0 && frames >= SettleFrames)
            {
                shotDone = false; shotError = null;
                if (DesignEditor.Instance is { } editor) editor.StartCoroutine(Shoot().WrapToIl2Cpp());
                else throw new InvalidOperationException("no editor to run photo coroutine");
                step = 1; frames = 0;
            }
            else if (step == 1 && (shotDone || frames > 120))
            {
                Restore();
                if (shotDone && shotError == null)
                {
                    long kb = File.Exists(file) ? new FileInfo(file).Length / 1024 : 0;
                    Plugin.ModLog.LogInfo($"TOOL_PHOTO saved {file} ({kb} KB)");
                    DesignEditor.Instance?.Say("照片已保存：" + file, 6);
                }
                else
                {
                    Plugin.ModLog.LogError($"TOOL_PHOTO not saved: {shotError?.ToString() ?? "the frame never came"}");
                    DesignEditor.Instance?.Say("照片未保存：" + (shotError?.Message ?? "画面始终未就绪"), 6);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"TOOL_PHOTO failed: {ex}");
            Restore();
        }
    }

    static bool shotDone;
    static Exception? shotError;

    /// The finished frame, as shown on screen, saved as a PNG. (The game's own ScreenCapture.CaptureScreenshot can't be
    /// called from a mod: its file name doesn't pass through.)
    static System.Collections.IEnumerator Shoot()
    {
        yield return new WaitForEndOfFrame(); // after everything is drawn
        Texture2D? shot = null;
        try
        {
            shot = ScreenCapture.CaptureScreenshotAsTexture();
            var pixels = shot.GetPixels32();
            var rgb = new byte[checked(pixels.Length * 3)];
            int nonOpaque = 0;
            for (int p = 0; p < pixels.Length; p++)
            {
                var pixel = pixels[p];
                rgb[p * 3] = pixel.r; rgb[p * 3 + 1] = pixel.g; rgb[p * 3 + 2] = pixel.b;
                if (pixel.a != 255) nonOpaque++;
            }
            // HDRP's final RGB already contains smoke blended over the scene, but its alpha may still contain
            // particle coverage/distortion values. Encoding that alpha makes viewers blend the smoke AGAIN.
            // Save an opaque RGB photograph without multiplying or compositing its already-finished colours.
            // GetPixels32 and SavePng both use bottom-up rows, so the original orientation is preserved.
            Drawing.SavePng(file, shot.width, shot.height, rgb);
            Plugin.ModLog.LogInfo($"TOOL_PHOTO opaque RGB output; discarded render alpha on {nonOpaque} pixels");
        }
        catch (Exception ex) { shotError = ex; }
        finally { if (shot != null) UnityEngine.Object.Destroy(shot); }
        shotDone = true;
    }

    /// Settings back as they were, and the overlay if it was showing.
    static void Restore()
    {
        step = -1;
        try { if (app != null && before != null) app.ApplySettings(before); }
        catch (Exception ex) { Plugin.ModLog.LogError($"TOOL_PHOTO couldn't put the graphics settings back (reopen Settings to fix): {ex}"); }
        try { if (overlay != null && overlayWas) overlay.SetOverlayVisible(true); } catch { }
        before = null;
    }

    /// A copy of the player's settings with its own graphics settings (the copy's may be shared with the original).
    static SettingsProfile Profile(SettingsProfile source, GraphicsSettings graphics)
    {
        var copy = new SettingsProfile(source);
        copy.graphics = graphics;
        return copy;
    }

    /// Every quality setting at its best. Looks (vignette, film grain, blur, depth of field on or off), resolution and
    /// anti-aliasing type stay as the player set them.
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
        g.DynamicResolutionMode = DynamicResolutionMode.Off; // full resolution, no upscaling
        g.DLSS = false;
        // Levels without a known top value: the game's "Maximum" preset's, unless the player's are better already
        // (the presets run quality 3 at Minimum to 0 at Maximum, terrain and shader the other way).
        var presets = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GraphicsSettingsContainer>())
            .Select(o => o.TryCast<GraphicsSettingsContainer>()).Where(p => p?.Settings != null).ToList();
        if (presets.FirstOrDefault(p => p!.Name == "Maximum")?.Settings is not { } best) return;
        g.QualityLevel = Math.Min(g.QualityLevel, best.QualityLevel);
        g.ShaderQualityLevel = Math.Max(g.ShaderQualityLevel, best.ShaderQualityLevel);
        g.TerrainQualityLevel = Math.Max(g.TerrainQualityLevel, best.TerrainQualityLevel);
        Plugin.ModLog.LogInfo($"TOOL_PHOTO presets {string.Join(", ", presets.Select(p => $"{p!.Name} (quality {p.Settings.QualityLevel}, shader {p.Settings.ShaderQualityLevel}, terrain {p.Settings.TerrainQualityLevel})"))}");
    }
}
