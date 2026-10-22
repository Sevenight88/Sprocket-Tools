using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SprocketTools;

/// The game's own UI pieces (uGUI with its font) for the mod's windows: a full-screen overlay canvas that takes the
/// clicks, and positioned nodes, labels and buttons on it, laid out from the top left in pixels of a 1100 x 760 screen.
internal static class NativeUi
{
    internal static TMP_FontAsset? Font() => TMP_Settings.defaultFontAsset ?? UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>()?.font;

    /// A screen-covering canvas over everything, with a dark shield that keeps clicks from reaching the game.
    internal static GameObject Canvas(string name)
    {
        var canvasObject = new GameObject(name, new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
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
        return canvasObject;
    }

    internal static RectTransform Node(string name, Transform parent, float x, float y, float width, float height)
    {
        var go = new GameObject(name, new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
        var rect = go.GetComponent<RectTransform>(); rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    internal static TextMeshProUGUI Label(TMP_FontAsset? font, Transform parent, string text, float x, float y, float width, float height, float size)
    {
        var rect = Node("Label", parent, x, y, width, height);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>(); label.font = font; label.fontSize = size;
        label.color = new Color(0.93f, 0.93f, 0.92f); label.raycastTarget = false;
        label.enableWordWrapping = true; label.overflowMode = TextOverflowModes.Ellipsis;
        label.richText = false;
        label.text = text; label.alignment = TextAlignmentOptions.TopLeft;
        return label;
    }

    /// A button; its callback goes into `keep` (the game must not lose it while the button lives).
    internal static Button Click(TMP_FontAsset? font, List<UnityAction> keep, Transform parent, string label, float x, float y, float width, float height, Action action, bool leftAligned = false)
    {
        var rect = Node(label.Length == 0 ? "Button" : label, parent, x, y, width, height);
        var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(0.2f, 0.22f, 0.24f);
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        var text = Label(font, rect, label, 8, 2, width - 16, height - 4, 16);
        text.alignment = leftAligned ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        var callback = Ui.Callback(action); keep.Add(callback); button.onClick.AddListener(callback);
        return button;
    }
}
