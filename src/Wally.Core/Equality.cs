namespace Wally.Core;

/// <summary>
/// Structural equality helpers. C# record equality compares reference-type
/// members (like IReadOnlyList) by reference, whereas the Python originals used
/// tuples and got value equality for free. These overrides restore value
/// semantics for the records that hold collections, so a config loaded from disk
/// compares equal to the one that produced it.
/// </summary>
internal static class SeqEquality
{
    public static bool ListEquals<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static int ListHash<T>(IReadOnlyList<T> items)
    {
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

public sealed partial record MonitorSetup
{
    public bool Equals(MonitorSetup? other)
        => other is not null && SeqEquality.ListEquals(Monitors, other.Monitors);

    public override int GetHashCode() => SeqEquality.ListHash(Monitors);
}

public sealed partial record SetupRule
{
    public bool Equals(SetupRule? other)
        => other is not null
           && SetupFingerprint == other.SetupFingerprint
           && DefaultImage == other.DefaultImage
           && DefaultFit == other.DefaultFit
           && SeqEquality.ListEquals(PerMonitor, other.PerMonitor);

    public override int GetHashCode()
        => HashCode.Combine(
            SetupFingerprint,
            DefaultImage,
            DefaultFit,
            SeqEquality.ListHash(PerMonitor));
}

public sealed partial record WallyConfig
{
    public bool Equals(WallyConfig? other)
        => other is not null
           && NewDesktopBehavior == other.NewDesktopBehavior
           && GalleryFolder == other.GalleryFolder
           && SeqEquality.ListEquals(SetupRules, other.SetupRules)
           && SeqEquality.ListEquals(DesktopAssignments, other.DesktopAssignments)
           && SeqEquality.ListEquals(RotationPool, other.RotationPool);

    public override int GetHashCode()
        => HashCode.Combine(
            NewDesktopBehavior,
            GalleryFolder,
            SeqEquality.ListHash(SetupRules),
            SeqEquality.ListHash(DesktopAssignments),
            SeqEquality.ListHash(RotationPool));
}
