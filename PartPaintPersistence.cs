namespace SprocketTools;

/// Bounds for the native saved-paint constructor, whose default array also controls
/// how many saved slots it loads. Extra entries are safe default placeholders;
/// the native constructor replaces them with the corresponding saved jobs.
internal static class PartPaintPersistence
{
    internal static T[]? NormalizeDefaults<T>(T[]? defaults, int savedCount,
        int firstUserSlot, int lastUserSlot, bool hasQoLOwners) where T : class
    {
        // The caller checks for a valid saved QoL job in a user slot. Other saved
        // IDs may legitimately be -1/missing for unused slots, so don't reject them.
        if (defaults == null || defaults.Length == 0 || !hasQoLOwners
            || firstUserSlot < 0 || lastUserSlot < firstUserSlot || lastUserSlot == int.MaxValue
            || savedCount <= defaults.Length || savedCount <= firstUserSlot
            || savedCount > lastUserSlot + 1) return defaults;

        T? fallback = null;
        foreach (var entry in defaults)
            if (entry != null) { fallback = entry; break; }
        if (fallback == null) return defaults;

        var extended = new T[savedCount];
        Array.Copy(defaults, extended, defaults.Length);
        Array.Fill(extended, fallback, defaults.Length, savedCount - defaults.Length);
        return extended;
    }
}
