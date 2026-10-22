namespace SprocketTools;

public enum ObjExportCategory
{
    Exterior, Engine, Powertrain, Transmission, Ammunition, InternalFuel, Crew, Sights, TurretMotors, LayingDrives
}

/// Part registry component IDs classify physical parts independently of their user-defined name.
public static class ObjExportPolicy
{
    // 字典的键是部件注册表 GUID，Classify 里比较的是组件 ID，两者都不参与翻译；值里的 Name 只给菜单列表与搜索用。
    public static readonly IReadOnlyDictionary<string, (string Name, ObjExportCategory Category)> KnownParts =
        new Dictionary<string, (string Name, ObjExportCategory Category)>(StringComparer.OrdinalIgnoreCase)
    {
        ["ebcf5eda-e50b-4f18-a063-0138a9d630e0"] = ("引擎", ObjExportCategory.Engine),
        ["5081f19f-4029-4686-9b92-57261096a362"] = ("离合器制动动力系统", ObjExportCategory.Powertrain),
        ["6192f19f-4029-4686-9b92-57261096a362"] = ("双传动动力系统", ObjExportCategory.Powertrain),
        ["fbbe2fd4-9092-46c6-8657-8d5fbcd8186d"] = ("转向机构", ObjExportCategory.Powertrain),
        ["7a508b04-0d69-40d3-9f4c-92c02346b9de"] = ("纵向传动系统", ObjExportCategory.Transmission),
        ["6b417b04-0d69-40d3-9f4c-92c02346b9de"] = ("横向传动系统", ObjExportCategory.Transmission),
        ["85b83f8c-b64b-43e4-8785-24cde9fccc1d"] = ("弹药架", ObjExportCategory.Ammunition),
        ["5e8ab5c7-e9f1-4c64-a04a-29efc78b1918"] = ("内部油箱", ObjExportCategory.InternalFuel),
        ["ecd3341c-f605-4816-946c-591eaa7e4f7d"] = ("外挂油桶", ObjExportCategory.Exterior),
        ["4481135e-cefd-499c-8122-414942eecbe4"] = ("乘员位置", ObjExportCategory.Crew),
        ["f90f373f-4edb-4563-a162-6eee6a5d77dd"] = ("炮手瞄准镜", ObjExportCategory.Sights),
        ["147d4042-4a13-4477-9adf-12e8291481e0"] = ("炮塔方向机", ObjExportCategory.TurretMotors),
        ["0c396ede-f39b-46ac-9d7e-e37c22b2c89e"] = ("瞄准驱动", ObjExportCategory.LayingDrives),
        ["ef551781-05bc-4aff-902a-372bdf0b3621"] = ("履带总成", ObjExportCategory.Exterior)
    };

    public static bool DefaultIncluded(ObjExportCategory category) => category == ObjExportCategory.Exterior;

    public static ObjExportCategory Classify(string? guid, IEnumerable<string?> componentIds, bool isInternal)
    {
        var components = componentIds.Where(c => c != null).ToHashSet(StringComparer.Ordinal);
        if (components.Contains("combustionEngine")) return ObjExportCategory.Engine;
        if (components.Contains("powertrain") || components.Contains("powertrainSteering")) return ObjExportCategory.Powertrain;
        if (components.Contains("transmission")) return ObjExportCategory.Transmission;
        if (components.Contains("ammoRack") || components.Contains("ammoRackModel")) return ObjExportCategory.Ammunition;
        if (components.Contains("fuelTank") && isInternal) return ObjExportCategory.InternalFuel;
        if (components.Contains("crewSeat") || components.Contains("postureUnityArmature")) return ObjExportCategory.Crew;
        if (components.Contains("sight")) return ObjExportCategory.Sights;
        if (components.Contains("traverseMotor") || components.Contains("traverseMotorModel")) return ObjExportCategory.TurretMotors;
        if (components.Contains("layingDrive")) return ObjExportCategory.LayingDrives;
        return KnownParts.TryGetValue(guid ?? "", out var known) ? known.Category : ObjExportCategory.Exterior;
    }
}
