using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Paints the seabed with the Coral Reef Decor Pack using the Terrain's own Trees and Details,
/// the same system as the Terrain "Paint Trees" / "Paint Details" brushes. After running it you
/// can keep painting or erasing by hand: select the Terrain, open the Paint Trees or Paint Details
/// tab and pick a prototype.
///
/// What goes where (seabed height, seabed Y 0 = 18 m deep, surface Y 18):
///   Sunlit shelf (Y >= 12)   : coral reef clusters (brain, table, staghorn, soft corals), kelp, rocks, shells, starfish
///   Twilight slope (4 - 12)  : sea fans (gorgonians), tube sponges, sea pens, lots of rock
///   Midnight floor (Y < 4)   : sparse deep-water corals, sea pens, scattered rocks and pebbles
/// Pebbles and rubble are spread everywhere so no zone looks clean.
///
/// Menu: SwitchShark > Paint Seabed (Coral Reef pack). Safe to run again: it replaces the painted
/// trees and details (prototypes from the original terrain stay available for hand painting).
/// </summary>
public static class SeabedPainter
{
    const string Pack = "Assets/Coral Reef Decor Pack/Coral Reef Decor Pack - URP/Assets/Prefabs/SM_Seabed_";
    const float ShelfY = 12f, DeepY = 4f;

    enum Zone { Deep, Slope, Shelf }

    class Rule
    {
        public string[] prefabs;
        public Zone[] zones;
        public int count;
        public Vector2 scale;
        public float maxSlope = 35f;
        public float cluster;     // 0 = spread evenly, 1 = only in clumps
        public int clusterSeed;
    }

    // Larger pieces: terrain trees.
    static readonly Rule[] Trees =
    {
        // Sunlit reef: corals grow in clumps on the shelf.
        new Rule { prefabs = new[] { "004_a", "004_b", "020_a", "020_b" }, zones = new[] { Zone.Shelf }, count = 110, scale = new Vector2(0.8f, 1.8f), cluster = 0.7f, clusterSeed = 1 },
        new Rule { prefabs = new[] { "005_a", "005_b", "010_a", "010_b" }, zones = new[] { Zone.Shelf }, count = 90, scale = new Vector2(0.8f, 1.6f), cluster = 0.7f, clusterSeed = 1 },
        new Rule { prefabs = new[] { "003", "008", "024", "009_a", "009_b" }, zones = new[] { Zone.Shelf }, count = 160, scale = new Vector2(0.7f, 1.5f), cluster = 0.7f, clusterSeed = 1 },
        new Rule { prefabs = new[] { "001", "007" }, zones = new[] { Zone.Shelf }, count = 70, scale = new Vector2(0.7f, 1.3f), cluster = 0.6f, clusterSeed = 1 },
        new Rule { prefabs = new[] { "023_a", "023_b" }, zones = new[] { Zone.Shelf }, count = 120, scale = new Vector2(0.5f, 0.85f), cluster = 0.8f, clusterSeed = 2 }, // kelp is 2.5 m tall: keep it under the surface
        // Twilight slope: sea fans and sponges on the rocky slope.
        new Rule { prefabs = new[] { "012", "013", "014", "015", "016_a", "016_b" }, zones = new[] { Zone.Slope }, count = 170, scale = new Vector2(0.8f, 1.8f), maxSlope = 45f, cluster = 0.4f, clusterSeed = 3 },
        new Rule { prefabs = new[] { "017_a", "017_b" }, zones = new[] { Zone.Slope, Zone.Shelf }, count = 70, scale = new Vector2(0.7f, 1.4f), maxSlope = 40f, cluster = 0.5f, clusterSeed = 4 },
        new Rule { prefabs = new[] { "018_a", "018_b" }, zones = new[] { Zone.Slope, Zone.Deep }, count = 90, scale = new Vector2(0.7f, 1.3f), cluster = 0.6f, clusterSeed = 5 },
        // Midnight floor: sparse cold-water corals.
        new Rule { prefabs = new[] { "030", "006" }, zones = new[] { Zone.Deep }, count = 60, scale = new Vector2(0.8f, 1.6f), cluster = 0.5f, clusterSeed = 6 },
        // Rocks everywhere, more on the slope.
        new Rule { prefabs = new[] { "019_a", "019_b", "022_a", "022_b", "028_a", "028_b", "011" }, zones = new[] { Zone.Slope }, count = 220, scale = new Vector2(1f, 3.5f), maxSlope = 50f },
        new Rule { prefabs = new[] { "019_a", "019_b", "022_a", "022_b", "028_a", "028_b", "011" }, zones = new[] { Zone.Deep }, count = 110, scale = new Vector2(0.8f, 3f) },
        new Rule { prefabs = new[] { "022_a", "022_b", "028_a", "028_b" }, zones = new[] { Zone.Shelf }, count = 70, scale = new Vector2(0.8f, 2.5f), cluster = 0.3f, clusterSeed = 1 },
    };

    // Small pieces: terrain details (instanced meshes, very dense, drawn near the camera).
    class DetailRule
    {
        public string prefab;
        public Zone[] zones;
        public int density;       // instances per detail cell at full strength
        public Vector2 scale;
        public float patchiness;  // 0 = everywhere, 1 = only in patches
        public int seed;
    }

    static readonly DetailRule[] Details =
    {
        new DetailRule { prefab = "021_a", zones = new[] { Zone.Deep, Zone.Slope, Zone.Shelf }, density = 2, scale = new Vector2(0.5f, 1.3f), patchiness = 0.4f, seed = 11 },
        new DetailRule { prefab = "021_b", zones = new[] { Zone.Deep, Zone.Slope, Zone.Shelf }, density = 2, scale = new Vector2(0.5f, 1.3f), patchiness = 0.4f, seed = 12 },
        new DetailRule { prefab = "025",   zones = new[] { Zone.Shelf }, density = 1, scale = new Vector2(0.7f, 1.2f), patchiness = 0.6f, seed = 13 },
        new DetailRule { prefab = "026",   zones = new[] { Zone.Shelf, Zone.Slope }, density = 1, scale = new Vector2(0.7f, 1.2f), patchiness = 0.6f, seed = 14 },
        new DetailRule { prefab = "027",   zones = new[] { Zone.Shelf, Zone.Slope, Zone.Deep }, density = 1, scale = new Vector2(0.3f, 0.5f), patchiness = 0.7f, seed = 15 },
    };

    const int DetailResolution = 256;
    static readonly Vector2 StartPatch = new Vector2(60f, 17.7f);

    [MenuItem("SwitchShark/Paint Seabed (Coral Reef pack)")]
    public static void PaintMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Debug.Log(Paint());
    }

    public static string Paint()
    {
        Terrain terrain = Object.FindAnyObjectByType<Terrain>();
        TerrainData data = terrain.terrainData;
        Undo.RegisterCompleteObjectUndo(data, "Paint seabed");

        // --- Tree prototypes: keep the original corals for hand painting, add the pack pieces ---
        var treeProtos = data.treePrototypes.Where(p => p.prefab != null && !p.prefab.name.StartsWith("SM_Seabed_")).ToList();
        int packStart = treeProtos.Count;
        var treeIndex = new Dictionary<string, int>();
        foreach (string id in Trees.SelectMany(r => r.prefabs).Distinct())
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{Pack}{id}.prefab");
            if (prefab == null) continue;
            treeIndex[id] = treeProtos.Count;
            treeProtos.Add(new TreePrototype { prefab = prefab, bendFactor = 0f });
        }
        data.treePrototypes = treeProtos.ToArray();

        // --- Tree instances ---
        var trees = new List<TreeInstance>();
        var rng = new System.Random(77);
        foreach (Rule rule in Trees)
        {
            int placed = 0;
            for (int attempt = 0; attempt < rule.count * 60 && placed < rule.count; attempt++)
            {
                float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                if (!Allowed(data, terrain, u, v, rule.zones, rule.maxSlope, out float y)) continue;
                if (rule.cluster > 0f && Clump(u, v, rule.clusterSeed) < rule.cluster * (float)rng.NextDouble()) continue;

                string id = rule.prefabs[rng.Next(rule.prefabs.Length)];
                if (!treeIndex.TryGetValue(id, out int index)) continue;
                float s = Mathf.Lerp(rule.scale.x, rule.scale.y, (float)rng.NextDouble());
                trees.Add(new TreeInstance
                {
                    prototypeIndex = index,
                    position = new Vector3(u, y / data.size.y, v),
                    widthScale = s,
                    heightScale = s * Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble()),
                    rotation = (float)rng.NextDouble() * Mathf.PI * 2f,
                    color = Color.white,
                    lightmapColor = Color.white,
                });
                placed++;
            }
        }
        data.SetTreeInstances(trees.ToArray(), true);

        // --- Detail prototypes: keep the original ones, add pebbles, shells and starfish ---
        var detailProtos = data.detailPrototypes.Where(p => p.prototype == null || !p.prototype.name.StartsWith("SM_Seabed_")).ToList();
        int keptDetails = detailProtos.Count;
        foreach (DetailRule d in Details)
        {
            detailProtos.Add(new DetailPrototype
            {
                prototype = AssetDatabase.LoadAssetAtPath<GameObject>($"{Pack}{d.prefab}.prefab"),
                usePrototypeMesh = true,
                renderMode = DetailRenderMode.VertexLit,
                useInstancing = true,
                minWidth = d.scale.x, maxWidth = d.scale.y,
                minHeight = d.scale.x, maxHeight = d.scale.y,
                noiseSpread = 0.3f,
                healthyColor = Color.white, dryColor = new Color(0.85f, 0.85f, 0.8f),
                alignToGround = 1f,
                positionJitter = 1f,
            });
        }
        data.detailPrototypes = detailProtos.ToArray();
        data.SetDetailResolution(DetailResolution, 16);

        int detailCount = 0;
        for (int layer = 0; layer < detailProtos.Count; layer++)
        {
            var map = new int[DetailResolution, DetailResolution];
            if (layer >= keptDetails)
            {
                DetailRule d = Details[layer - keptDetails];
                for (int z = 0; z < DetailResolution; z++)
                for (int x = 0; x < DetailResolution; x++)
                {
                    float u = (x + 0.5f) / DetailResolution, v = (z + 0.5f) / DetailResolution;
                    if (!Allowed(data, terrain, u, v, d.zones, 40f, out _)) continue;
                    float patch = Clump(u, v, d.seed);
                    if (patch < d.patchiness * 0.6f) continue;
                    map[z, x] = Mathf.RoundToInt(d.density * Mathf.Clamp01(patch * 1.5f));
                    detailCount += map[z, x];
                }
            }
            data.SetDetailLayer(0, 0, layer, map);
        }

        terrain.detailObjectDistance = 70f;
        terrain.treeDistance = 200f;
        terrain.treeBillboardDistance = 200f; // no billboards: these prefabs are meshes with LODs
        EditorUtility.SetDirty(terrain);
        EditorUtility.SetDirty(data);
        EditorSceneManager.MarkSceneDirty(terrain.gameObject.scene);
        EditorSceneManager.SaveScene(terrain.gameObject.scene);
        AssetDatabase.SaveAssets();

        return $"SeabedPainter: {trees.Count} trees from {treeIndex.Count} pack pieces, about {detailCount} small details " +
               $"({treeProtos.Count - packStart} tree and {Details.Length} detail prototypes added).";
    }

    static bool Allowed(TerrainData data, Terrain terrain, float u, float v, Zone[] zones, float maxSlope, out float y)
    {
        y = data.GetInterpolatedHeight(u, v);
        float worldY = y + terrain.transform.position.y;
        Zone zone = worldY >= ShelfY ? Zone.Shelf : worldY < DeepY ? Zone.Deep : Zone.Slope;
        if (System.Array.IndexOf(zones, zone) < 0) return false;
        if (data.GetSteepness(u, v) > maxSlope) return false;
        if (worldY > OceanZones.SurfaceY - 2.5f) return false; // keep a swim gap under the surface

        var p = new Vector2(u * data.size.x, v * data.size.z) + new Vector2(terrain.transform.position.x, terrain.transform.position.z);
        return Vector2.Distance(p, StartPatch) > 4f; // the shark's start spot stays clear
    }

    /// <summary>0..1 noise that makes things grow in clumps rather than evenly.</summary>
    static float Clump(float u, float v, int seed) =>
        Mathf.Clamp01((Mathf.PerlinNoise(u * 9f + seed * 17.3f, v * 9f + seed * 5.1f) - 0.3f) / 0.5f);
}
