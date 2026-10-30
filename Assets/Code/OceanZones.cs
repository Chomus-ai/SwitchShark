using UnityEngine;

/// <summary>
/// The level's three ocean zones and how game height maps to real ocean depth.
/// The water column is 18 m tall (seabed Y 0, surface Y 18) and split into thirds:
///   top third     Sunlight zone   0 - 200 m real depth
///   middle third  Twilight zone   200 - 1000 m
///   bottom third  Midnight zone   1000 - 1800 m
/// </summary>
public static class OceanZones
{
    public enum Zone { Sunlight, Twilight, Midnight }

    public const float SeabedY = 0f;
    public const float SurfaceY = 18f;

    // Real-ocean depth at the zone boundaries (top of each zone, then the seabed).
    static readonly float[] OceanDepthAtBoundary = { 0f, 200f, 1000f, 1800f };

    /// <summary>0 at the surface, 1 at the seabed.</summary>
    public static float DepthFraction(float worldY) =>
        Mathf.Clamp01((SurfaceY - worldY) / (SurfaceY - SeabedY));

    /// <summary>Metres below the surface in game units.</summary>
    public static float GameDepth(float worldY) => DepthFraction(worldY) * (SurfaceY - SeabedY);

    /// <summary>The real ocean depth this height stands for, in metres.</summary>
    public static float OceanDepth(float worldY)
    {
        float t = DepthFraction(worldY) * 3f;
        int i = Mathf.Min(2, Mathf.FloorToInt(t));
        return Mathf.Lerp(OceanDepthAtBoundary[i], OceanDepthAtBoundary[i + 1], t - i);
    }

    public static Zone ZoneAt(float worldY) => (Zone)Mathf.Min(2, Mathf.FloorToInt(DepthFraction(worldY) * 3f));

    public static string Name(Zone zone) => zone switch
    {
        Zone.Sunlight => "SUNLIGHT ZONE",
        Zone.Twilight => "TWILIGHT ZONE",
        _ => "MIDNIGHT ZONE",
    };
}
