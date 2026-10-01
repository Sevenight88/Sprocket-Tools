using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons.Editor;

namespace SprocketTools;

/// Firepower: shows the gun's length in calibers (L/xx) in the Cannon panel. L counts from the muzzle to the face of the
/// breech block, as guns are measured: the barrel plus the chamber, which holds the whole round. In the game the round is
/// its propellant plus a 3-caliber shell (CannonBlueprint.ShellLength), sitting behind the barrel segments, and
/// BoreLength is the barrel plus that round (the same 3 calibers the game fires: CannonConfig.ProjectileLength). The
/// barrel is the sum of its segments; a muzzle device is a separate part and isn't counted, as for real guns.
[HarmonyPatch]
public static class GunLength
{
    [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    static void Draw(CannonEditor __instance, IGUILayout layout) => Ui.Guard("Gun length", () =>
    {
        var gun = __instance.Component?.Blueprint;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (gun == null || ui == null || gun.Caliber == 0) return;
        int barrel = gun.BarrelLength, round = gun.ShellLength, propellant = gun.PropellantLength;
        Ui.Section(layout, "火炮长度");
        ui.InfoField($"L/{Calibers(gun.BoreLength, gun.Caliber)}   （{gun.BoreLength} 毫米，炮口至炮闩面；口径 {gun.Caliber} 毫米）\n" +
                     $"炮管 {barrel} 毫米（L/{Calibers(barrel, gun.Caliber)}）+ 药室 {round} 毫米\n" +
                     $"完整弹药 {round} 毫米：发射药 {propellant} + 弹体 {round - propellant}（3 倍口径）\n" +
                     "炮口制退器不计入，与真实火炮量法一致", 4);
    });

    static string Calibers(int lengthMm, int caliberMm) => caliberMm > 0 ? (lengthMm / (float)caliberMm).ToString("0.##") : "0";
}
