using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Rebuilds the SwitchShark level as a small demo ocean with three zones laid out
/// like a real continental margin, deepest first:
///   Deep basin (midnight)  : Z 0..~45   abyssal plain at the 18 m seabed, gentle hills, rocky basin walls
///   Slope      (twilight)  : Z ~45..~75 continental slope with gullies and one submarine canyon
///   Shelf      (sunlight)  : Z ~75..120 sandy shelf 3-5 m below the surface, ripples and reef outcrops
/// The shark starts on the deep floor facing the slope, so rising also means swimming
/// toward the light.
///
/// Writes a NEW TerrainData (Assets/Environment/OceanFloor/OceanFloor.asset) and assigns it
/// to the scene's terrain; the old "New Terrain.asset" is left untouched. Also updates the
/// depth-dependent values in the scene (GameManager, AscentAtmosphere, FishSpawner,
/// SharkBoundary, fish swim ceilings) to match.
///
/// Run from the menu: SwitchShark > Build Ocean Floor. Safe to run again.
/// </summary>
public static class OceanFloorBuilder
{
    // ---------- Layout (metres, world space) ----------
    const float Size = 120f;          // play area: X and Z from 0 to Size (SharkBoundary)
    const float Margin = 60f;         // extra seabed around the play area, so its edges fade into the fog
    const float TerrainSize = Size + 2f * Margin;
    const float WaterDepth = 18f;     // seabed (deepest) at Y = 0, surface at Y = WaterDepth
    const int Resolution = 1025;      // heightmap samples per side (~0.23 m apart)

    const float DeepFloorY = 0.4f;
    const float SlopeStartZ = 45f;
    const float SlopeEndZ = 75f;
    const float ShelfY = WaterDepth - 5f; // 5 m deep at the shelf break...
    const float ShelfRise = 2f;       // ...rising to 3 m deep at the far edge
    const float MaxSeabedY = WaterDepth - 1.5f;

    const float CanyonX = 84f;
    const float CanyonDepth = 5f;
    const float CanyonHalfWidth = 5f;

    static readonly Vector3 SharkStart = new Vector3(60f, 0f, 16f); // Y is snapped to the seabed

    const string ScenePath = "Assets/Scenes/SwitchShark.unity";
    const string OutputFolder = "Assets/Environment/OceanFloor";
    const string TerrainDataPath = OutputFolder + "/OceanFloor.asset";
    const string SourceTerrainPath = "Assets/New Terrain.asset";
    const string TerrainMaterialPath = OutputFolder + "/OceanFloorTerrain.mat";
    const string SandColourPath = "Assets/Textures/GroundSand005/GroundSand005_COL_2K.jpg";
    const string SandNormalPath = "Assets/Textures/GroundSand005/GroundSand005_NRM_2K.jpg";

    const float CoralScale = 0.25f;   // the coral models are 2-9 m wide; the shark is 0.6 m long

    [MenuItem("SwitchShark/Build Ocean Floor")]
    public static void BuildMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
    }

    /// <summary>Entry point for batch mode: -executeMethod OceanFloorBuilder.BuildFromCommandLine</summary>
    public static void BuildFromCommandLine()
    {
        Build();
    }

    static void Build()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        Terrain terrain = Object.FindAnyObjectByType<Terrain>();
        if (terrain == null)
        {
            Debug.LogError("OceanFloorBuilder: no Terrain in the scene.");
            return;
        }

        // Corals and plants are copied from the original terrain, also when re-running.
        TerrainData old = terrain.terrainData;
        if (old == null || AssetDatabase.GetAssetPath(old) == TerrainDataPath)
            old = AssetDatabase.LoadAssetAtPath<TerrainData>(SourceTerrainPath);
        TerrainData data = BuildTerrainData(old);

        terrain.terrainData = data;
        terrain.materialTemplate = TerrainMaterial();
        terrain.transform.position = new Vector3(-Margin, 0f, -Margin);
        var collider = terrain.GetComponent<TerrainCollider>();
        if (collider != null) collider.terrainData = data;
        EditorUtility.SetDirty(terrain);

        PlaceShark(terrain);
        UpdateGameplaySettings();
        MoveSceneryToMap();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();

        Debug.Log($"OceanFloorBuilder: done. Play area {Size} x {Size} m inside {TerrainSize} x {TerrainSize} m of seabed, depth {WaterDepth} m, " +
                  $"terrain heights {MinHeight(data):0.0}..{MaxHeight(data):0.0} m.");
    }

    // ---------------------------------------------------------------------
    // Terrain
    // ---------------------------------------------------------------------

    static TerrainData BuildTerrainData(TerrainData old)
    {
        if (!AssetDatabase.IsValidFolder(OutputFolder))
            AssetDatabase.CreateFolder("Assets/Environment", "OceanFloor");

        var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
        if (data == null)
        {
            data = new TerrainData();
            AssetDatabase.CreateAsset(data, TerrainDataPath);
        }

        data.heightmapResolution = Resolution;
        data.size = new Vector3(TerrainSize, WaterDepth, TerrainSize);

        var heights = new float[Resolution, Resolution];
        for (int iz = 0; iz < Resolution; iz++)
        for (int ix = 0; ix < Resolution; ix++)
        {
            float x = -Margin + ix / (Resolution - 1f) * TerrainSize;
            float z = -Margin + iz / (Resolution - 1f) * TerrainSize;
            heights[iz, ix] = Mathf.Clamp01(Height(x, z) / WaterDepth);
        }
        data.SetHeights(0, 0, heights);

        PaintZones(data);
        CopyAndScatterPlants(old, data);

        EditorUtility.SetDirty(data);
        return data;
    }

    /// <summary>Seabed height (world Y) at a point.</summary>
    static float Height(float x, float z)
    {
        // The shelf break wanders, so the slope isn't a straight wall.
        float wobble = (Fbm(x * 0.025f, 11.3f, 2) - 0.5f) * 14f;
        float slopeStart = SlopeStartZ + wobble;
        float slopeEnd = SlopeEndZ + wobble;

        float t = Mathf.Clamp01(Mathf.InverseLerp(slopeStart, slopeEnd, z));
        float shelf = ShelfProfile(t);                     // 0 = deep basin, 1 = shelf
        float slope = Mathf.Clamp01(4f * t * (1f - t));    // peaks halfway down the slope
        float deep = 1f - shelf;

        float y = Mathf.Lerp(DeepFloorY, ShelfY, shelf);
        y += Mathf.InverseLerp(slopeEnd, Size, z) * ShelfRise;

        // Deep basin: low abyssal hills and the occasional mound.
        y += (Fbm(x * 0.035f, z * 0.035f, 3) - 0.45f) * 1.6f * deep;
        y += Mathf.Pow(Mathf.Max(0f, Fbm(x * 0.08f + 40f, z * 0.08f, 2) - 0.62f) / 0.38f, 1.5f) * 2.5f * deep;

        // Slope: downslope gullies.
        float ridge = 1f - Mathf.Abs(2f * Mathf.PerlinNoise(x * 0.09f, 3.7f + z * 0.012f) - 1f);
        y -= ridge * ridge * ridge * 1.6f * slope;

        // Submarine canyon cutting through the slope and notching the shelf edge.
        float canyonCentre = CanyonX + (Mathf.PerlinNoise(z * 0.04f, 91f) - 0.5f) * 10f;
        float across = (x - canyonCentre) / CanyonHalfWidth;
        float canyonMask = Smooth(Mathf.InverseLerp(slopeStart - 12f, slopeStart + 4f, z))
                         * (1f - Smooth(Mathf.InverseLerp(slopeEnd - 2f, slopeEnd + 10f, z)));
        y -= Mathf.Exp(-across * across) * CanyonDepth * canyonMask;

        // Shelf: reef outcrops, rubble and sand ripples.
        float reef = Mathf.Max(0f, Fbm(x * 0.05f + 200f, z * 0.05f, 3) - 0.55f) / 0.45f;
        y += Mathf.Pow(reef, 0.7f) * 2.2f * shelf;
        y += (Fbm(x * 0.35f, z * 0.35f + 70f, 2) - 0.5f) * 0.5f * reef * shelf;
        float ripplePhase = (x * 0.35f + z * 0.94f) * (2f * Mathf.PI / 1.3f) + Mathf.PerlinNoise(x * 0.06f, z * 0.06f) * 6f;
        y += Mathf.Sin(ripplePhase) * 0.06f * shelf * (1f - reef);

        // Rocky walls at the outer edges of the terrain (well outside the play area, in the fog).
        float edge = Mathf.Min(x + Margin, Size + Margin - x, z + Margin);
        float wall = 1f - Smooth(Mathf.InverseLerp(0f, 14f, edge));
        y += wall * (4f + Fbm(x * 0.12f, z * 0.12f + 300f, 3) * 3f) * (1f - shelf * 0.8f);

        // Flat sandy patch where the shark rests at the start.
        float start = Smooth(Mathf.InverseLerp(4f, 10f, Vector2.Distance(new Vector2(x, z), new Vector2(SharkStart.x, SharkStart.z))));
        y = Mathf.Lerp(DeepFloorY, y, start);

        return Mathf.Clamp(y, 0f, MaxSeabedY);
    }

    /// <summary>Continental margin profile: a long gentle rise at the foot, steepest high up, rounded shelf break.</summary>
    static float ShelfProfile(float t) => Smooth(Mathf.Pow(t, 1.4f));

    static float Smooth(float t) => t * t * (3f - 2f * t);

    static float Fbm(float x, float z, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Mathf.PerlinNoise(x, z) * amp;
            norm += amp;
            x = x * 2.03f + 17.1f;
            z = z * 2.03f + 5.7f;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    // ---------------------------------------------------------------------
    // Texturing: sunlit sand on the shelf, rock on steep ground, dark silt in the basin
    // ---------------------------------------------------------------------

    /// <summary>URP terrain material, so the three zone layers below actually show.</summary>
    static Material TerrainMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
            AssetDatabase.CreateAsset(material, TerrainMaterialPath);
        }
        return material;
    }

    static void PaintZones(TerrainData data)
    {
        var colour = AssetDatabase.LoadAssetAtPath<Texture2D>(SandColourPath);
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(SandNormalPath);
        TerrainLayer sand = MakeLayer("ShelfSand", colour, normal, new Color(1.0f, 0.97f, 0.88f), 4f);
        TerrainLayer rock = MakeLayer("SlopeRock", colour, normal, new Color(0.40f, 0.40f, 0.42f), 1.5f);
        TerrainLayer silt = MakeLayer("DeepSilt", colour, normal, new Color(0.30f, 0.34f, 0.40f), 6f);
        data.terrainLayers = new[] { sand, rock, silt };

        int res = 1024; // ~0.23 m per texel over the 240 m terrain
        data.alphamapResolution = res;
        var maps = new float[res, res, 3];
        for (int iz = 0; iz < res; iz++)
        for (int ix = 0; ix < res; ix++)
        {
            float u = ix / (res - 1f), v = iz / (res - 1f);
            float y = data.GetInterpolatedHeight(u, v);
            float steep = data.GetSteepness(u, v);

            float shallow = Smooth(Mathf.InverseLerp(DeepFloorY + 3f, ShelfY - 1f, y));
            float rocky = Smooth(Mathf.InverseLerp(18f, 32f, steep));
            float noise = Fbm(u * 25f, v * 25f, 2) * 0.3f;

            float r = Mathf.Clamp01(rocky + noise * rocky);
            float s = (1f - r) * shallow;
            float d = (1f - r) * (1f - shallow);
            float sum = Mathf.Max(0.0001f, r + s + d);
            maps[iz, ix, 0] = s / sum;
            maps[iz, ix, 1] = r / sum;
            maps[iz, ix, 2] = d / sum;
        }
        data.SetAlphamaps(0, 0, maps);
    }

    static TerrainLayer MakeLayer(string name, Texture2D colour, Texture2D normal, Color tint, float tile)
    {
        string path = $"{OutputFolder}/{name}.terrainlayer";
        var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
        if (layer == null)
        {
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, path);
        }
        layer.diffuseTexture = colour;
        layer.normalMapTexture = normal;
        layer.maskMapTexture = null;
        layer.normalScale = 0.6f;
        layer.smoothness = 0.1f;
        layer.tileSize = new Vector2(tile, tile);
        layer.diffuseRemapMin = Vector4.zero;
        layer.diffuseRemapMax = new Vector4(tint.r, tint.g, tint.b, 1f);
        EditorUtility.SetDirty(layer);
        return layer;
    }

    // ---------------------------------------------------------------------
    // Corals / plants: reuse the old terrain's tree and detail prototypes
    // ---------------------------------------------------------------------

    static void CopyAndScatterPlants(TerrainData old, TerrainData data)
    {
        if (old == null || old == data) return;

        data.SetTreeInstances(new TreeInstance[0], false); // drop trees painted with other prototypes first
        data.treePrototypes = old.treePrototypes;
        var trees = new List<TreeInstance>();
        if (old.treePrototypes.Length > 0)
        {
            float width = old.treeInstanceCount > 0 ? old.treeInstances.Average(t => t.widthScale) : 1f;
            float height = old.treeInstanceCount > 0 ? old.treeInstances.Average(t => t.heightScale) : 1f;
            var rng = new System.Random(7);
            for (int attempt = 0; attempt < 6000 && trees.Count < 260; attempt++)
            {
                float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                float y = data.GetInterpolatedHeight(u, v);
                // Corals live in the sunlit shallows, thinning out down the upper slope.
                float chance = Smooth(Mathf.InverseLerp(ShelfY - 5f, ShelfY + 1f, y));
                if (data.GetSteepness(u, v) > 30f || rng.NextDouble() > chance * 0.5f) continue;

                float s = CoralScale * (0.7f + (float)rng.NextDouble() * 0.6f);
                trees.Add(new TreeInstance
                {
                    prototypeIndex = rng.Next(old.treePrototypes.Length),
                    position = new Vector3(u, y / WaterDepth, v),
                    widthScale = width * s,
                    heightScale = height * s,
                    rotation = (float)rng.NextDouble() * Mathf.PI * 2f,
                    color = Color.white,
                    lightmapColor = Color.white,
                });
            }
        }
        data.SetTreeInstances(trees.ToArray(), true);

        // Small coral meshes are scattered on the shelf sand at coral scale.
        // Billboard (texture) details are kept but not painted.
        DetailPrototype[] details = old.detailPrototypes;
        foreach (DetailPrototype d in details)
        {
            if (!d.usePrototypeMesh) continue;
            d.minWidth *= CoralScale;  d.maxWidth *= CoralScale;
            d.minHeight *= CoralScale; d.maxHeight *= CoralScale;
        }
        data.detailPrototypes = details;
        if (details.Length > 0)
        {
            data.SetDetailResolution(256, 16);
            for (int layer = 0; layer < details.Length; layer++)
            {
                if (!details[layer].usePrototypeMesh)
                {
                    data.SetDetailLayer(0, 0, layer, new int[256, 256]);
                    continue;
                }
                var map = new int[256, 256];
                for (int iz = 0; iz < 256; iz++)
                for (int ix = 0; ix < 256; ix++)
                {
                    float u = ix / 255f, v = iz / 255f;
                    float y = data.GetInterpolatedHeight(u, v);
                    float patch = Fbm(u * 12f + layer * 31f, v * 12f, 2);
                    bool shallowSand = y > ShelfY - 1.5f && data.GetSteepness(u, v) < 15f;
                    map[iz, ix] = shallowSand && patch > 0.55f ? 3 : 0;
                }
                data.SetDetailLayer(0, 0, layer, map);
            }
        }

        Debug.Log($"OceanFloorBuilder: {trees.Count} corals placed from {old.treePrototypes.Length} tree prototypes, " +
                  $"{old.detailPrototypes.Length} detail prototypes copied.");
    }

    static float MinHeight(TerrainData d) => d.GetHeights(0, 0, d.heightmapResolution, d.heightmapResolution).Cast<float>().Min() * d.size.y;
    static float MaxHeight(TerrainData d) => d.GetHeights(0, 0, d.heightmapResolution, d.heightmapResolution).Cast<float>().Max() * d.size.y;

    // ---------------------------------------------------------------------
    // Scene objects
    // ---------------------------------------------------------------------

    /// <summary>Puts the shark on the deep floor facing the slope, and brings the camera rig along.</summary>
    static void PlaceShark(Terrain terrain)
    {
        SharkRise shark = Object.FindAnyObjectByType<SharkRise>();
        if (shark == null)
        {
            Debug.LogWarning("OceanFloorBuilder: no SharkRise in the scene, shark not moved.");
            return;
        }

        Transform root = shark.transform;
        Vector3 oldPos = root.position;
        Vector3 oldForward = Vector3.ProjectOnPlane(root.forward, Vector3.up);
        float oldYaw = oldForward.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(oldForward).eulerAngles.y : 0f;

        // Keep the shark's belly just above the sand.
        float pivotAboveBelly = 0f;
        var renderers = root.GetComponentsInChildren<Renderer>().Where(r => !(r is ParticleSystemRenderer)).ToArray();
        if (renderers.Length > 0)
        {
            Bounds b = renderers[0].bounds;
            foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
            pivotAboveBelly = oldPos.y - b.min.y;
        }

        float ground = terrain.SampleHeight(SharkStart) + terrain.transform.position.y;
        Vector3 newPos = new Vector3(SharkStart.x, ground + pivotAboveBelly + 0.05f, SharkStart.z);
        Quaternion turn = Quaternion.Euler(0f, 0f - oldYaw, 0f); // face +Z, toward the slope

        // Cameras that frame the shark keep the same offset from it.
        var rig = new HashSet<Transform>();
        foreach (Camera c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            rig.Add(c.transform);
        foreach (MonoBehaviour m in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            if (m.GetType().Name.Contains("Cinemachine")) rig.Add(m.transform);

        foreach (Transform t in rig)
        {
            if (t.IsChildOf(root)) continue;
            Undo.RecordObject(t, "Move camera");
            t.SetPositionAndRotation(newPos + turn * (t.position - oldPos), turn * t.rotation);
        }

        Undo.RecordObject(root, "Move shark");
        root.SetPositionAndRotation(newPos, turn * root.rotation);

        Debug.Log($"OceanFloorBuilder: shark moved from {oldPos} to {newPos}, {rig.Count} camera objects moved with it.");
    }

    static void UpdateGameplaySettings()
    {
        float scale = WaterDepth / 125f; // the old level was 125 m deep

        foreach (GameManager gm in Object.FindObjectsByType<GameManager>(FindObjectsInactive.Include))
            Set(gm, so =>
            {
                so.FindProperty("minHeight").floatValue = 0f;
                so.FindProperty("maxHeight").floatValue = WaterDepth;
            });

        foreach (AscentAtmosphere a in Object.FindObjectsByType<AscentAtmosphere>(FindObjectsInactive.Include))
            Set(a, so =>
            {
                so.FindProperty("bottomY").floatValue = 0f;
                so.FindProperty("surfaceY").floatValue = WaterDepth;
                // Three vertical bands that match the terrain zones:
                // deep look in the bottom third, blend through the middle, shallow look in the top third.
                var curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.33f, 0f), new Keyframe(0.67f, 1f), new Keyframe(1f, 1f));
                for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
                so.FindProperty("curve").animationCurveValue = curve;
            });

        foreach (SharkBoundary b in Object.FindObjectsByType<SharkBoundary>(FindObjectsInactive.Include))
            Set(b, so =>
            {
                so.FindProperty("minX").floatValue = 0f;
                so.FindProperty("maxX").floatValue = Size;
                so.FindProperty("minZ").floatValue = 0f;
                so.FindProperty("maxZ").floatValue = Size;
                so.FindProperty("softMargin").floatValue = 10f;
                so.FindProperty("gizmoHeight").floatValue = WaterDepth;
            });

        var fishPrefabs = new HashSet<GameObject>();
        foreach (FishSpawner f in Object.FindObjectsByType<FishSpawner>(FindObjectsInactive.Include))
            Set(f, so =>
            {
                so.FindProperty("surfaceHeight").floatValue = WaterDepth;
                so.FindProperty("surfaceMargin").floatValue = 1f;
                ClampMax(so.FindProperty("lateralRange"), 20f);
                ClampMax(so.FindProperty("despawnBehindDistance"), 15f);
                ClampMax(so.FindProperty("despawnMaxDistance"), 60f);

                SerializedProperty species = so.FindProperty("species");
                for (int i = 0; i < species.arraySize; i++)
                {
                    SerializedProperty s = species.GetArrayElementAtIndex(i);
                    SerializedProperty band = s.FindPropertyRelative("heightRange");
                    if (band.vector2Value.y > WaterDepth + 0.01f) // still in old 125 m units
                    {
                        band.vector2Value = Vector2.Min(band.vector2Value * scale, Vector2.one * WaterDepth);
                        ClampMax(s.FindPropertyRelative("fadeMargin"), 1f);
                    }
                    if (s.FindPropertyRelative("prefab").objectReferenceValue is GameObject p) fishPrefabs.Add(p);
                }
            });

        // Fish swim ceilings live on the prefabs.
        foreach (GameObject prefab in fishPrefabs)
            foreach (SimpleFishSwim swim in prefab.GetComponentsInChildren<SimpleFishSwim>(true))
                Set(swim, so =>
                {
                    ClampMax(so.FindProperty("ceilingY"), WaterDepth);
                    ClampMax(so.FindProperty("ceilingFade"), 1.5f);
                });
    }

    /// <summary>Scenery positioned for the old 1 km map is pulled over the new one.</summary>
    static void MoveSceneryToMap()
    {
        var centre = new Vector3(Size * 0.5f, WaterDepth * 0.5f, Size * 0.5f);

        foreach (GameObject go in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
                     .Where(t => t.parent == null).Select(t => t.gameObject))
        {
            if (go.name == "Skybox_mesh")
            {
                Undo.RecordObject(go.transform, "Move sky");
                go.transform.position = new Vector3(centre.x, go.transform.position.y, centre.z);
            }
            else if (go.TryGetComponent(out AudioSource audio) && audio.spatialBlend > 0f)
            {
                Undo.RecordObject(go.transform, "Move audio");
                go.transform.position = centre;
            }
        }
    }

    static void Set(Object target, System.Action<SerializedObject> edit)
    {
        var so = new SerializedObject(target);
        edit(so);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    static void ClampMax(SerializedProperty p, float max)
    {
        if (p != null && p.floatValue > max) p.floatValue = max;
    }
}
