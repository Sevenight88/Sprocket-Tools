namespace SprocketTools;

/// A gun belongs to its closest trunnions, never to every ancestor mount in the vehicle.
public static class DrawingMounts
{
    public const string TrunnionGuid = "e11ce433-c0b3-4e52-899f-898bfe6d3fd3";
    public record Part(int Parent, bool Trunnion, bool Turret);

    public static int Owner(int gun, IReadOnlyDictionary<int, Part> parts)
    {
        var visited = new HashSet<int>();
        for (int id = gun; id >= 0 && visited.Add(id) && parts.TryGetValue(id, out var part); id = part.Parent)
        {
            if (part.Trunnion) return id;
            // A directly mounted turret gun must not borrow a mount outside its own turret.
            if (part.Turret) return -1;
        }
        return -1;
    }

    public static int? SelectGun(int mount, IEnumerable<(int Id, int Caliber)> guns,
        IReadOnlyDictionary<int, Part> parts, Func<int, bool> included) =>
        guns.Where(g => included(g.Id) && Owner(g.Id, parts) == mount)
            .OrderByDescending(g => g.Caliber).Select(g => (int?)g.Id).FirstOrDefault();
}
