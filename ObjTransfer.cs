using System.Globalization;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Factions;
using Sprocket.UI;
using Sprocket.VehicleDesigner;
using Sprocket.Vehicles.PlateStructures.Design;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketTools;

/// A modal, native UI menu. Its raycast shield and suspended designer action maps keep clicks and typing
/// in the menu from changing the tank. No IMGUI input or assumptions about the game's inspector layout.
[HarmonyPatch]
public static class ObjTransfer
{
    static GameObject? canvasObject, panel, rows;
    static readonly List<UnityAction> listeners = new();
    static readonly List<UnityAction> rowListeners = new();
    static readonly List<UnityAction<string>> textListeners = new();
    static readonly List<InputActionMap> suspended = new();
    static readonly Dictionary<ObjExportCategory, TextMeshProUGUI> categoryLabels = new();
    static List<ObjExportPart> parts = new();
    static readonly HashSet<int> selected = new();
    static TextMeshProUGUI? summary, pageLabel, categoryFilterLabel, messageLabel;
    static Button? previous, next, exportButton;
    static TMP_FontAsset? font;
    static IntPtr target;
    static IntPtr inputTarget, inputEditor;
    static bool importTab, rebuild, rebuildRows, dialog;
    static int page, releaseAt, factionIndex;
    static string search = "", message = "", directory = "", importPath = "";
    static string blueprintName = "", factionDirectory = "";
    static readonly List<(string Name, string Directory)> factions = new();
    static ObjExportCategory? filter;
    static ObjDocument? imported;
    static Task<string?>? picker;
    static Action<string>? picked;
    static float importScale = 1, armourMm = 10;
    static bool mergePlates = true;
    static Action? queued;
    const int PageSize = 12;
    const int LargeImport = 50_000; // faces; the largest structures known to work in the game have about 30,000
    internal static bool IsOpen => canvasObject != null;
    internal static bool BlocksInput => IsOpen || dialog || Time.frameCount <= releaseAt;

    [HarmonyPrefix, HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.UpdatePointerOperators))]
    static bool BlockPointer() => !BlocksInput;

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Inspector(IGUILayout layout) => Ui.Inspector("OBJ transfer", layout, () =>
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null) return;
        Ui.Section(layout, "OBJ 导出与导入");
        ui.InfoField($"导出选中部件为 OBJ，或把 OBJ 导入成可编辑的板结构。按 {Keybinds.Shown("obj")} 打开菜单。", 2);
        var tip = new UITooltip("OBJ 导出与导入", "选择导出类别与单个部件。把 OBJ 导入成可编辑的板结构，存进所选派系的图库。导出的部件保持相对位置、朝向与大小不变。");
        ui.Button($"OBJ 导出 / 导入（{Keybinds.Shown("obj")}）", Ui.Callback(() => queued = Open), ref tip);
    });

    /// 菜单自己的按键，默认 Ctrl+O：功能键那一排都被出图和光照占用了，Steam 叠加层还会截走 F11、F12，
    /// 所以这里带一个修饰键；设计器的操作表里 Ctrl+O 是空的。可以在「Mod Options」或按键窗口里改。
    static bool Hotkey() => Keybinds.Pressed("obj");

    internal static void Update()
    {
        if (picker?.IsCompleted == true)
        {
            var task = picker; var completion = picked;
            picker = null; picked = null; dialog = false;
            try
            {
                var path = task.GetAwaiter().GetResult();
                if (IsOpen && DesignEditor.Instance?.Core?.Target?.Pointer == target && path != null) completion?.Invoke(path);
            }
            catch (Exception ex) { Failed(ex); }
        }
        if (!IsOpen && !dialog && Time.frameCount > releaseAt) RestoreInputs();
        if (IsOpen && (DesignEditor.Instance?.IsReady != true || DesignEditor.Instance.Core?.Target?.Pointer != target))
        { Close(); return; }
        var keys = Keyboard.current;
        if (IsOpen && !dialog)
        {
            if (Hotkey() || keys?.escapeKey.wasPressedThisFrame == true)
            { Close(); return; }
        }
        else if (!dialog && Hotkey() && DesignEditor.Instance?.IsReady == true && !MeshTools.Typing()) Open();
        if (queued != null)
        {
            var action = queued; queued = null;
            try { action(); }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }
        if (!IsOpen) return;
        if (rebuild) { rebuild = false; BuildPanel(); }
        else if (rebuildRows && !importTab) { rebuildRows = false; RefreshRows(); }
    }

    internal static void LeftEditor() { Close(); RestoreInputs(); imported = null; }

    static void Failed(Exception ex)
    {
        message = ex.Message;
        Plugin.ModLog.LogError($"OBJ transfer: {ex}");
        DesignEditor.Instance?.Say("OBJ: " + message, 10);
        if (IsOpen) rebuild = true;
    }

    static bool Ready()
    {
        var editor = DesignEditor.Instance;
        if (editor?.IsReady != true) { editor?.Say("先在编辑器里打开一辆载具。"); return false; }
        if (editor.IsBusy || editor.Core?.Editor?.OperationInProgress == true || editor.Core?.DesignIOPossible != true)
        { editor.Say("等当前编辑器操作结束，再打开 OBJ 菜单。"); return false; }
        return !editor.CaptureBlocked();
    }

    static void Open()
    {
        if (IsOpen || !Ready()) return;
        target = DesignEditor.Instance!.Core!.Target.Pointer;
        parts = ObjTankExport.CollectParts(DesignEditor.Instance).OrderBy(p => p.Category).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();
        Defaults(); search = ""; filter = null; page = 0; message = "";
        imported = null; importPath = ""; importTab = false;
        FindFactions();
        if (directory.Length == 0) directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Models");
        try
        {
            SuspendInputs();
            font = TMP_Settings.defaultFontAsset;
            if (font == null)
                font = UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>()?.font;
            if (font == null) throw new InvalidOperationException("游戏的界面字体还没准备好；选中一个结构后再试一次。");
            canvasObject = new GameObject("SprocketTools OBJ menu", new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30000;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1100, 760);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            canvasObject.AddComponent<GraphicRaycaster>();
            var shade = Node("Modal input shield", canvasObject.transform, 0, 0, 0, 0);
            shade.anchorMin = Vector2.zero; shade.anchorMax = Vector2.one; shade.sizeDelta = Vector2.zero;
            shade.gameObject.AddComponent<Image>().color = new Color(0.015f, 0.02f, 0.03f, 0.86f);
            BuildPanel();
        }
        catch { Close(); throw; }
    }

    static void SuspendInputs()
    {
        if (suspended.Count > 0) return;
        inputTarget = DesignEditor.Instance?.Core?.Target?.Pointer ?? IntPtr.Zero;
        inputEditor = DesignEditor.Instance?.Core?.Editor?.Pointer ?? IntPtr.Zero;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>()))
        {
            var asset = o.TryCast<InputActionAsset>();
            if (asset?.FindActionMap("MeshEdit", false) == null) continue;
            // UI uses its own maps; only designer maps are suspended, with their exact prior enabled state retained.
            foreach (string name in new[] { "Core", "MeshEdit", "Camera" })
                if (asset.FindActionMap(name, false) is { enabled: true } map && !suspended.Any(m => m.Pointer == map.Pointer))
                { suspended.Add(map); map.Disable(); }
        }
    }

    static void RestoreInputs()
    {
        var editor = DesignEditor.Instance;
        // A load or scene transition takes ownership of its new controls. Do not revive old maps there.
        if (editor?.IsReady == true && editor.Core?.Target?.Pointer == inputTarget && editor.Core?.Editor?.Pointer == inputEditor)
            foreach (var map in suspended)
                try { map.Enable(); } catch (Exception ex) { Plugin.ModLog.LogWarning($"OBJ menu: could not restore an old input map: {ex.Message}"); }
        suspended.Clear();
        inputTarget = inputEditor = IntPtr.Zero;
    }

    static void Close()
    {
        if (canvasObject != null) { canvasObject.SetActive(false); UnityEngine.Object.Destroy(canvasObject); }
        canvasObject = panel = rows = null; font = null; target = IntPtr.Zero;
        summary = pageLabel = categoryFilterLabel = messageLabel = null;
        previous = next = exportButton = null;
        categoryLabels.Clear(); listeners.Clear(); rowListeners.Clear(); textListeners.Clear();
        parts.Clear(); selected.Clear(); imported = null; queued = null;
        rebuild = rebuildRows = false;
        releaseAt = Time.frameCount + 2; // the final close/import click must not fall through into the editor
    }

    static void Defaults()
    { selected.Clear(); foreach (var p in parts) if (ObjTankExport.DefaultIncluded(p.Category)) selected.Add(p.Id); }

    static string Title(ObjExportCategory category) => category switch
    {
        ObjExportCategory.Exterior => "外部与其他部件",
        ObjExportCategory.Engine => "引擎",
        ObjExportCategory.Powertrain => "动力系统",
        ObjExportCategory.Transmission => "传动系统",
        ObjExportCategory.Ammunition => "弹药",
        ObjExportCategory.InternalFuel => "内部油箱",
        ObjExportCategory.Crew => "乘员",
        ObjExportCategory.Sights => "炮手瞄准镜",
        ObjExportCategory.TurretMotors => "炮塔方向机",
        ObjExportCategory.LayingDrives => "瞄准驱动",
        _ => category.ToString(),
    };

    static void BuildPanel()
    {
        if (panel != null) { panel.SetActive(false); UnityEngine.Object.Destroy(panel); }
        rows = null;
        listeners.Clear(); rowListeners.Clear(); textListeners.Clear(); categoryLabels.Clear();
        summary = pageLabel = categoryFilterLabel = messageLabel = null; previous = next = exportButton = null;
        var rect = Node("OBJ menu panel", canvasObject!.transform, 0, 0, 1080, 730);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        panel = rect.gameObject;
        panel.AddComponent<Image>().color = new Color(0.08f, 0.09f, 0.105f, 1);
        Label(panel.transform, "OBJ 导出与导入", 22, 14, 850, 30, 24);
        Click(panel.transform, $"关闭（{Keybinds.Shown("obj")} / Esc）", 882, 12, 176, 34, Close);
        Click(panel.transform, "导出载具", 22, 60, 170, 34, () => { importTab = false; rebuild = true; });
        Click(panel.transform, "导入模型", 202, 60, 170, 34, () => { importTab = true; rebuild = true; });
        if (importTab) BuildImport(); else BuildExport();
        messageLabel = Label(panel.transform, message, 22, 674, 1036, 45, 15);
    }

    static void BuildExport()
    {
        Label(panel!.transform, "每个勾选的部件都保持在车上的原位。OBJ 以米为单位、Y 轴朝上。板结构只导出外形（无厚度），可直接导回编辑。", 22, 105, 1030, 30, 17);
        Label(panel.transform, "导出类别", 22, 142, 278, 30, 19);
        float y = 176;
        foreach (var category in Enum.GetValues<ObjExportCategory>())
        {
            var c = category;
            var button = Click(panel.transform, "", 22, y, 278, 32, () =>
            {
                var ids = parts.Where(p => p.Category == c).Select(p => p.Id).ToArray();
                bool all = ids.All(selected.Contains);
                foreach (int id in ids) { if (all) selected.Remove(id); else selected.Add(id); }
                rebuildRows = true;
            });
            categoryLabels[category] = button.GetComponentInChildren<TextMeshProUGUI>();
            y += 36;
        }
        Label(panel.transform, "默认不勾选你列出的那些内部设备。外挂油箱仍然默认勾选。", 22, 542, 278, 68, 15);
        Click(panel.transform, "默认", 22, 616, 88, 32, () => { Defaults(); rebuildRows = true; });
        Click(panel.transform, "全选", 117, 616, 88, 32, () => { selected.Clear(); foreach (var p in parts) selected.Add(p.Id); rebuildRows = true; });
        Click(panel.transform, "清空", 212, 616, 88, 32, () => { selected.Clear(); rebuildRows = true; });
        TextInput(panel.transform, search, "搜索部件…", 322, 142, 432, 34, value => { search = value; page = 0; rebuildRows = true; });
        var filterButton = Click(panel.transform, "", 764, 142, 294, 34, () =>
        {
            var categories = Enum.GetValues<ObjExportCategory>();
            int index = filter == null ? -1 : Array.IndexOf(categories, filter.Value);
            filter = index + 1 >= categories.Length ? null : categories[index + 1];
            page = 0; rebuildRows = true;
        });
        categoryFilterLabel = filterButton.GetComponentInChildren<TextMeshProUGUI>();
        summary = Label(panel.transform, "", 322, 180, 734, 27, 16);
        previous = Click(panel.transform, "上一页", 322, 616, 92, 32, () => { page--; rebuildRows = true; });
        next = Click(panel.transform, "下一页", 420, 616, 92, 32, () => { page++; rebuildRows = true; });
        pageLabel = Label(panel.transform, "", 524, 616, 172, 32, 15);
        Click(panel.transform, "用编辑器里的所选", 708, 616, 176, 32, () =>
        {
            var editorSelection = DesignEditor.Instance!.SelectedParts().ToHashSet();
            selected.Clear(); foreach (var p in parts) if (editorSelection.Contains(p.Id)) selected.Add(p.Id);
            rebuildRows = true;
        });
        exportButton = Click(panel.transform, "导出 OBJ…", 894, 616, 164, 32, () => queued = Export);
        RefreshRows();
    }

    static void RefreshRows()
    {
        if (rows != null) { rows.SetActive(false); UnityEngine.Object.Destroy(rows); }
        rowListeners.Clear();
        var list = parts.Where(p => (filter == null || p.Category == filter) &&
            (search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Id.ToString(CultureInfo.InvariantCulture).Contains(search))).ToArray();
        int pages = Math.Max(1, (list.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        rows = Node("Parts", panel!.transform, 322, 214, 736, 396).gameObject;
        for (int i = page * PageSize; i < Math.Min(list.Length, (page + 1) * PageSize); i++)
        {
            var p = list[i];
            Click(rows.transform, $"{(selected.Contains(p.Id) ? "[x]" : "[ ]")}  {p.Name}  ·  #{p.Id}", 0, (i % PageSize) * 32, 736, 29,
                () => { if (!selected.Add(p.Id)) selected.Remove(p.Id); rebuildRows = true; }, leftAligned: true, row: true);
        }
        if (list.Length == 0) Label(rows.transform, "没有匹配的部件。换个搜索词或类别试试。", 0, 12, 730, 45, 16);
        summary!.text = $"已选 {selected.Count} / {parts.Count} 个部件   ·   当前筛选显示 {list.Length} 个";
        pageLabel!.text = $"第 {page + 1} / {pages} 页";
        categoryFilterLabel!.text = filter == null ? "类别：全部（点击切换）" : Title(filter.Value) + "（点选下一个）";
        previous!.interactable = page > 0; next!.interactable = page + 1 < pages;
        exportButton!.interactable = selected.Count > 0;
        foreach (var (category, label) in categoryLabels)
        {
            var group = parts.Where(p => p.Category == category).ToArray();
            int count = group.Count(p => selected.Contains(p.Id));
            label.text = $"{(count == 0 ? "[ ]" : count == group.Length ? "[x]" : "[-]")} {Title(category)}（{count}/{group.Length}）";
        }
    }

    static void BuildImport()
    {
        Label(panel!.transform, "导入 OBJ 到板结构图库", 22, 112, 1000, 35, 22);
        Label(panel.transform, "把所有对象按原始相对位置保存成一个可编辑结构。之后从游戏的板结构图库里选它，就能装到载具上。", 22, 156, 1030, 58, 17);
        Label(panel.transform, "保存到派系", 22, 224, 190, 34, 17);
        var before = Click(panel.transform, "上一个", 224, 222, 105, 36, () => ChangeFaction(-1));
        var after = Click(panel.transform, "下一个", 949, 222, 109, 36, () => ChangeFaction(1));
        before.interactable = after.interactable = factions.Count > 1;
        Label(panel.transform, factions.Count == 0 ? "没有找到派系。请先在游戏里建一个派系。" : factions[factionIndex].Name, 341, 224, 598, 36, 18);
        // Shown, not checked: a faction deleted while the menu is open is reported when saving, not by a broken panel.
        Label(panel.transform, factions.Count == 0 ? "" : Path.Combine(factionDirectory, "Blueprints", "Plate Structures"), 22, 271, 1036, 45, 15);
        Click(panel.transform, "选择 OBJ 文件…", 22, 325, 210, 40, () => queued = ChooseImport);
        Label(panel.transform, importPath.Length == 0 ? "还没有选文件" : importPath, 250, 325, 808, 45, 16);
        if (imported != null)
        {
            int vertices = imported.Meshes.Sum(m => m.Vertices.Length), faces = imported.Meshes.Sum(m => m.Faces.Length);
            Label(panel.transform, $"{imported.Meshes.Length} 个对象   ·   {vertices:N0} 个顶点   ·   {faces:N0} 个面", 22, 382, 1010, 28, 18);
            Label(panel.transform, "蓝图名称", 22, 423, 330, 34, 17);
            TextInput(panel.transform, blueprintName, "模型名称", 365, 423, 693, 34, value => blueprintName = value);
            Label(panel.transform, "缩放（1 = 原始米数）", 22, 469, 330, 34, 17);
            TextInput(panel.transform, Shown(importScale), "1", 365, 469, 200, 34, value => importScale = Number(value));
            Label(panel.transform, "板厚（整数毫米）", 22, 515, 330, 34, 17);
            TextInput(panel.transform, Shown(armourMm), "10", 365, 515, 200, 34, value => armourMm = Number(value));
            Click(panel.transform, mergePlates ? "[x] 合并共面的面" : "[ ] 合并共面的面", 22, 561, 290, 36,
                () => { mergePlates = !mergePlates; rebuild = true; }, leftAligned: true);
            Label(panel.transform, "只导入几何；贴图与能运作的载具机构不会导入。已有的蓝图文件不会被覆盖。", 330, 563, 702, 40, 16);
            var save = Click(panel.transform, "保存板结构", 22, 616, 300, 36, () => queued = Import);
            save.interactable = factions.Count > 0;
        }
    }

    /// A typed number, with a comma or a point for the decimals; NaN when it isn't one.
    static float Number(string text) =>
        float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : float.NaN;

    static string Shown(float value) => float.IsNaN(value) ? "" : value.ToString("G", CultureInfo.InvariantCulture);

    static void FindFactions()
    {
        factions.Clear();
        foreach (var faction in FactionIO.GetAllFactions())
        {
            string path = faction.Directory;
            if (Directory.Exists(path) && !factions.Any(f => string.Equals(f.Directory, path, StringComparison.OrdinalIgnoreCase)))
                factions.Add((faction.Name, path));
        }
        var current = FactionManager.CurrentFaction;
        if (current != null && Directory.Exists(current.Directory) &&
            !factions.Any(f => string.Equals(f.Directory, current.Directory, StringComparison.OrdinalIgnoreCase)))
            factions.Add((current.Name, current.Directory));
        factions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        // Keep a deliberate selection on reopening; otherwise start with the game's active faction.
        factionIndex = factions.FindIndex(f => string.Equals(f.Directory, factionDirectory, StringComparison.OrdinalIgnoreCase));
        if (factionIndex < 0) factionIndex = factions.FindIndex(f => string.Equals(f.Directory, current?.Directory, StringComparison.OrdinalIgnoreCase));
        if (factionIndex < 0) factionIndex = 0;
        factionDirectory = factions.Count == 0 ? "" : factions[factionIndex].Directory;
    }

    static void ChangeFaction(int direction)
    {
        if (factions.Count == 0) return;
        factionIndex = (factionIndex + direction + factions.Count) % factions.Count;
        factionDirectory = factions[factionIndex].Directory;
        rebuild = true;
    }

    static void Export()
    {
        var editor = DesignEditor.Instance!;
        if (editor.IsBusy || editor.CaptureBlocked()) return;
        if (ExplodedView.Active) throw new InvalidOperationException($"先关闭本菜单，按 {Keybinds.Shown("explode")} 把爆炸视图收回去再导出，这样每个部件都在原位。");
        if (selected.Count == 0) throw new InvalidOperationException("至少选择一个要导出的部件。");
        Directory.CreateDirectory(directory);
        string name = Conversion.Parse(editor.Snapshot())["header"]?["name"]?.GetValue<string>() ?? "Tank";
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var ids = selected.ToHashSet();
        picker = ObjFilePicker.SaveAsync(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.obj");
        dialog = true;
        picked = path =>
        {
            directory = Path.GetDirectoryName(path)!;
            message = ObjTankExport.Export(editor, ids, path);
            Plugin.ModLog.LogInfo("TOOL_OBJ_EXPORT: " + message + "; path=" + path);
            editor.Say(message, 10); rebuild = true;
        };
    }

    static void ChooseImport()
    {
        Directory.CreateDirectory(directory);
        picker = ObjFilePicker.OpenAsync(directory);
        dialog = true;
        picked = path =>
        {
            // Parse fully before saving a structure. Never trust mtllib paths or run external programs.
            if (new FileInfo(path).Length > ObjMeshFormat.MaxTextLength)
                throw new FormatException("OBJ 文件太大。最大 64 MiB。");
            var document = ObjMeshFormat.Read(File.ReadAllText(path));
            if (document.Meshes.Length == 0) throw new FormatException("这个 OBJ 里没有多边形对象。");
            imported = document; importPath = path; importScale = 1; armourMm = 10;
            blueprintName = ObjPlateFiles.BlueprintName(Path.GetFileNameWithoutExtension(path));
            directory = Path.GetDirectoryName(path)!; message = ""; rebuild = true;
        };
    }

    static void Import()
    {
        var editor = DesignEditor.Instance!;
        if (editor.IsBusy || editor.CaptureBlocked() || imported == null) return;
        if (!float.IsFinite(importScale) || importScale <= 0 || importScale > 1000)
            throw new FormatException("请输入一个大于 0、不超过 1000 的缩放。");
        if (!float.IsFinite(armourMm) || armourMm < 1 || armourMm > 1000 || armourMm != MathF.Truncate(armourMm))
            throw new FormatException("请输入 1 到 1000 之间的整数毫米板厚。");
        var document = imported;
        float scale = importScale, thickness = armourMm;
        if (factions.Count == 0) throw new FormatException("先选择一个已有的派系。");
        string name = ObjPlateFiles.BlueprintName(blueprintName);
        // Validate and serialize all geometry before publishing a library file. No tank snapshot or edit is needed.
        var result = ObjBlueprintImport.Build(document, name, scale, thickness, merge: mergePlates);
        string path = ObjPlateFiles.Save(factionDirectory, name, result.Json);
        // The merge count is the one number that says whether the shape came back editable, so it goes in the message.
        string merged = result.FacesBeforeMerge == 0 ? ""
            : result.FaceCount < result.FacesBeforeMerge ? $" 共面合并把 {result.FacesBeforeMerge:N0} 个面并成 {result.FaceCount:N0} 个。"
            : $" 共面合并没有并掉面。{(result.MergeWhy != null ? " " + result.MergeWhy : "")}";
        message = $"已把 {result.ObjectCount} 个对象（{result.FaceCount:N0} 个面）存成 {Path.GetFileName(path)}，放进派系 {factions[factionIndex].Name}。" + merged +
                  (result.SkippedFaces > 0 ? $" 跳过了 {result.SkippedFaces:N0} 个没有面积的面。" : "") +
                  (result.FaceCount > LargeImport ? " 这个结构非常大：游戏打开或编辑它时可能会变慢。" : "");
        Plugin.ModLog.LogInfo($"TOOL_OBJ_IMPORT: {message}; vertices={result.VertexCount}; faces={result.FaceCount}; merged={result.FacesBeforeMerge}->{result.FaceCount}; merge_why={result.MergeWhy ?? "-"}; skipped={result.SkippedFaces}; path={path}");
        editor.Say(message, 10); rebuild = true;
    }

    static RectTransform Node(string name, Transform parent, float x, float y, float width, float height)
    {
        var go = new GameObject(name, new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
        var rect = go.GetComponent<RectTransform>(); rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    static TextMeshProUGUI Label(Transform parent, string text, float x, float y, float width, float height, float size)
    {
        var rect = Node("Label", parent, x, y, width, height);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>(); label.font = font; label.fontSize = size;
        label.color = new Color(0.93f, 0.93f, 0.92f); label.raycastTarget = false;
        label.enableWordWrapping = true; label.overflowMode = TextOverflowModes.Ellipsis;
        label.richText = false;
        label.text = text; label.alignment = TextAlignmentOptions.TopLeft;
        return label;
    }

    static Button Click(Transform parent, string label, float x, float y, float width, float height, Action action, bool leftAligned = false, bool row = false)
    {
        var rect = Node(label.Length == 0 ? "Button" : label, parent, x, y, width, height);
        var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(0.2f, 0.22f, 0.24f);
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        var text = Label(rect, label, 8, 2, width - 16, height - 4, 16);
        text.alignment = leftAligned ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        var callback = Ui.Callback(action); (row ? rowListeners : listeners).Add(callback); button.onClick.AddListener(callback);
        return button;
    }

    static void TextInput(Transform parent, string text, string placeholder, float x, float y, float width, float height, Action<string> change)
    {
        var rect = Node("Text input", parent, x, y, width, height);
        var background = rect.gameObject.AddComponent<Image>();
        background.color = new Color(0.12f, 0.13f, 0.15f);
        var field = rect.gameObject.AddComponent<TMP_InputField>();
        field.targetGraphic = background;
        var viewport = Node("Clipped text viewport", rect, 8, 5, width - 16, height - 10);
        viewport.gameObject.AddComponent<RectMask2D>();
        field.textViewport = viewport;
        var value = Label(viewport, "", 0, 0, width - 16, height - 10, 16);
        value.enableWordWrapping = false; value.overflowMode = TextOverflowModes.Overflow;
        field.textComponent = value;
        var hint = Label(viewport, placeholder, 0, 0, width - 16, height - 10, 16);
        hint.color = new Color(0.6f, 0.63f, 0.68f);
        field.placeholder = hint; field.lineType = TMP_InputField.LineType.SingleLine;
        field.SetTextWithoutNotify(text);
        var callback = DelegateSupport.ConvertDelegate<UnityAction<string>>(change)!;
        textListeners.Add(callback); field.onValueChanged.AddListener(callback);
    }
}
