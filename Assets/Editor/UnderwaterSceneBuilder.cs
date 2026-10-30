using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds the immersive layer of the SwitchShark scene on top of the ocean floor:
///   - Underwater Atmosphere: water colour, fog, light, caustics and marine snow change per zone
///   - Sea Surface: rippling surface seen from below
///   - Seabed Props: rocks, cold-water corals and sponges chosen per zone; Jellyfish drifting mid-water
///   - HUD Canvas: minimap and depth gauge (top right), start prompt and controls card (bottom)
/// Menu: SwitchShark > Build Underwater Scene. Safe to run again: it replaces what it built before.
/// Run SwitchShark > Build Ocean Floor first if the terrain changes (the minimap is drawn from it).
/// </summary>
public static class UnderwaterSceneBuilder
{
    const string ScenePath = "Assets/Scenes/SwitchShark.unity";
    const string UiFolder = "Assets/UI/HUD";
    const string WaterFolder = "Assets/Environment/Water";
    const string SeabedFolder = "Assets/Environment/Seabed";
    const int TerrainLayer = 3;
    const float PlaySize = 120f; // play area X/Z 0..120 (SharkBoundary); the terrain extends beyond it

    [MenuItem("SwitchShark/Build Underwater Scene")]
    public static void BuildMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
    }

    public static string Build()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Folder(UiFolder); Folder(WaterFolder);

        Terrain terrain = Object.FindAnyObjectByType<Terrain>();
        Camera cam = Camera.main;
        Transform shark = Object.FindAnyObjectByType<SharkRise>().transform;

        BuildAtmosphere(cam, shark);
        BuildSeaSurface(terrain);
        int props = DressSeabed(terrain);
        int jellies = AddJellyfish(terrain);
        BuildHud(terrain, shark);
        BuildSwimEffects(shark, cam);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        string result = $"UnderwaterSceneBuilder: {props} seabed props, {jellies} jellyfish, atmosphere, sea surface and HUD built.";
        Debug.Log(result);
        return result;
    }

    // ---------------------------------------------------------------------
    // Atmosphere
    // ---------------------------------------------------------------------

    static void BuildAtmosphere(Camera cam, Transform shark)
    {
        GameObject go = Fresh("Underwater Atmosphere");
        var atmosphere = go.AddComponent<UnderwaterAtmosphere>();

        // Soft light that follows the camera, so prey is visible in the midnight zone.
        Transform oldLight = cam.transform.Find("Viewer Light");
        if (oldLight != null) Object.DestroyImmediate(oldLight.gameObject);
        var lightGo = new GameObject("Viewer Light");
        lightGo.transform.SetParent(cam.transform, false);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 20f;
        light.color = new Color(0.55f, 0.75f, 1f);
        light.shadows = LightShadows.None;

        // Marine snow around the camera; the old particle system on the shark used a Built-in shader.
        Transform oldSnow = cam.transform.Find("Marine Snow");
        if (oldSnow != null) Object.DestroyImmediate(oldSnow.gameObject);
        ParticleSystem snow = MakeMarineSnow(cam.transform);
        foreach (ParticleSystem ps in shark.GetComponentsInChildren<ParticleSystem>(true))
            ps.gameObject.SetActive(false);

        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include)
            .Where(l => l.type == LightType.Directional).ToArray();
        GameObject sky = GameObject.Find("Skybox_mesh");

        var so = new SerializedObject(atmosphere);
        SerializedProperty sun = so.FindProperty("sunLights");
        sun.arraySize = lights.Length;
        for (int i = 0; i < lights.Length; i++) sun.GetArrayElementAtIndex(i).objectReferenceValue = lights[i];
        so.FindProperty("backdrop").objectReferenceValue = sky != null ? sky.GetComponentInChildren<Renderer>() : null;
        so.FindProperty("viewerLight").objectReferenceValue = light;
        so.FindProperty("marineSnow").objectReferenceValue = snow;
        so.FindProperty("sky").objectReferenceValue = SkyMaterial();
        so.ApplyModifiedProperties();
    }

    static Material SkyMaterial()
    {
        string path = $"{WaterFolder}/UnderwaterSky.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("SwitchShark/Underwater Sky"));
            AssetDatabase.CreateAsset(m, path);
        }
        return m;
    }

    static ParticleSystem MakeMarineSnow(Transform parent)
    {
        var go = new GameObject("Marine Snow");
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.startLifetime = 10f;
        main.startSpeed = 0.02f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.045f);
        main.startColor = new Color(0.85f, 0.92f, 1f, 0.55f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 1500;
        main.prewarm = true;

        var emission = ps.emission;
        emission.rateOverTime = 30f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(24f, 14f, 24f);

        var velocity = ps.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = new ParticleSystem.MinMaxCurve(0f);
        velocity.y = new ParticleSystem.MinMaxCurve(-0.04f); // marine snow sinks slowly
        velocity.z = new ParticleSystem.MinMaxCurve(0f);

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.06f;
        noise.frequency = 0.25f;

        var fade = ps.colorOverLifetime;
        fade.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                         new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
        fade.color = gradient;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = MarineSnowMaterial();
        return ps;
    }

    // ---------------------------------------------------------------------
    // Shark swim effects: silt, glowing tail wake, water push, eat burst
    // ---------------------------------------------------------------------

    static void BuildSwimEffects(Transform shark, Camera cam)
    {
        Material dust = ParticleMaterial("SwimDust", additive: false);
        Material glow = ParticleMaterial("SwimGlow", additive: true);

        var effects = shark.GetComponent<SharkSwimEffects>();
        if (effects == null) effects = shark.gameObject.AddComponent<SharkSwimEffects>();
        var so = new SerializedObject(effects);
        so.FindProperty("shark").objectReferenceValue = shark.GetComponent<SharkRise>();
        so.FindProperty("tail").objectReferenceValue = shark.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "spine_11");
        so.FindProperty("dustMaterial").objectReferenceValue = dust;
        so.FindProperty("glowMaterial").objectReferenceValue = glow;
        Transform snow = cam.transform.Find("Marine Snow");
        so.FindProperty("marineSnow").objectReferenceValue = snow != null ? snow.GetComponent<ParticleSystem>() : null;
        so.ApplyModifiedProperties();

        var eater = shark.GetComponent<SharkEater>();
        if (eater != null)
        {
            var e = new SerializedObject(eater);
            e.FindProperty("eatEffectPrefab").objectReferenceValue = EatBurstPrefab(dust, glow);
            e.ApplyModifiedProperties();
        }
    }

    static Material ParticleMaterial(string name, bool additive)
    {
        string path = $"{WaterFolder}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Creatures/HDSharkAssets/soft_dot.png"));
        m.SetColor("_BaseColor", Color.white);
        MakeTransparent(m, additive);
        if (additive) m.EnableKeyword("_BLENDMODE_ADD"); else m.DisableKeyword("_BLENDMODE_ADD");
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>Burst of glinting scales and a little cloud where a fish is eaten.</summary>
    static GameObject EatBurstPrefab(Material dust, Material glow)
    {
        string path = $"{WaterFolder}/EatBurst.prefab";
        var go = new GameObject("EatBurst");
        try
        {
            var scales = go.AddComponent<ParticleSystem>();
            ConfigureBurst(scales, glow, 40, new Vector2(0.015f, 0.04f), new Vector2(0.3f, 1.3f), new Vector2(0.8f, 1.8f),
                           new Color(0.9f, 0.95f, 1f, 0.9f), grow: 0.6f);

            var cloudGo = new GameObject("Cloud");
            cloudGo.transform.SetParent(go.transform, false);
            var cloud = cloudGo.AddComponent<ParticleSystem>();
            ConfigureBurst(cloud, dust, 10, new Vector2(0.15f, 0.35f), new Vector2(0.05f, 0.3f), new Vector2(1.5f, 2.5f),
                           new Color(0.75f, 0.78f, 0.75f, 0.25f), grow: 2.5f);

            return PrefabUtility.SaveAsPrefabAsset(go, path);
        }
        finally { Object.DestroyImmediate(go); }
    }

    static void ConfigureBurst(ParticleSystem ps, Material material, int count, Vector2 size, Vector2 speed, Vector2 life, Color color, float grow)
    {
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.duration = 0.5f;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
        main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
        main.startColor = color;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0.01f;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.1f;

        var drag = ps.limitVelocityOverLifetime;
        drag.enabled = true;
        drag.limit = 0f;
        drag.dampen = 0.08f;

        var sizeOverLife = ps.sizeOverLifetime;
        sizeOverLife.enabled = true;
        sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, grow));

        var fade = ps.colorOverLifetime;
        fade.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                         new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.7f, 0.5f), new GradientAlphaKey(0f, 1f) });
        fade.color = gradient;

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.3f;
        noise.frequency = 0.6f;

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    static Material MarineSnowMaterial()
    {
        string path = $"{WaterFolder}/MarineSnow.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Creatures/HDSharkAssets/soft_dot.png"));
        MakeTransparent(m, additive: false);
        EditorUtility.SetDirty(m);
        return m;
    }

    // ---------------------------------------------------------------------
    // Sea surface
    // ---------------------------------------------------------------------

    static void BuildSeaSurface(Terrain terrain)
    {
        GameObject go = Fresh("Sea Surface");
        Object.DestroyImmediate(go);
        go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = "Sea Surface";
        Object.DestroyImmediate(go.GetComponent<Collider>());
        Vector3 size = terrain.terrainData.size;
        go.transform.position = terrain.transform.position + new Vector3(size.x * 0.5f, OceanZones.SurfaceY, size.z * 0.5f);
        go.transform.localScale = new Vector3(size.x * 2f / 10f, 1f, size.z * 2f / 10f); // plane is 10 m

        Texture2D ripples = RippleNormalMap();
        string path = $"{WaterFolder}/SeaSurface.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", new Color(0.62f, 0.86f, 0.9f, 0.25f)); // see-through, so the sky window shows
        m.SetFloat("_Smoothness", 0.95f);
        m.SetTexture("_BumpMap", ripples);
        m.SetTextureScale("_BumpMap", new Vector2(24f, 24f));
        m.SetFloat("_BumpScale", 0.8f);
        m.EnableKeyword("_NORMALMAP");
        m.SetTexture("_DetailNormalMap", ripples);
        m.SetTextureScale("_DetailNormalMap", new Vector2(57f, 57f));
        m.SetFloat("_DetailNormalMapScale", 0.6f);
        m.EnableKeyword("_DETAIL_MULX2");
        m.SetColor("_EmissionColor", new Color(0.1f, 0.2f, 0.22f)); // skylight coming through
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        MakeTransparent(m, additive: false);
        m.SetFloat("_Cull", 0f); // visible from below
        EditorUtility.SetDirty(m);

        go.GetComponent<Renderer>().sharedMaterial = m;
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.AddComponent<WaterSurface>();
    }

    static Texture2D RippleNormalMap()
    {
        string path = $"{WaterFolder}/SeaRipples_Normal.png";
        if (!File.Exists(path))
        {
            const int n = 256;
            var heights = new float[n, n];
            var waves = new[] { (1, 2, 0.5f), (3, -1, 0.3f), (-2, 3, 0.25f), (5, 4, 0.12f), (-6, 5, 0.1f), (8, -7, 0.06f) };
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = x / (float)n * 2f * Mathf.PI, v = y / (float)n * 2f * Mathf.PI, h = 0f;
                foreach (var (a, b, amp) in waves) h += Mathf.Sin(a * u + b * v + a * 1.3f) * amp; // integer frequencies tile seamlessly
                heights[x, y] = h;
            }
            var tex = new Texture2D(n, n, TextureFormat.RGB24, false);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = heights[(x + 1) % n, y] - heights[(x - 1 + n) % n, y];
                float dy = heights[x, (y + 1) % n] - heights[x, (y - 1 + n) % n];
                Vector3 normal = new Vector3(-dx * 4f, -dy * 4f, 1f).normalized;
                tex.SetPixel(x, y, new Color(normal.x * 0.5f + 0.5f, normal.y * 0.5f + 0.5f, normal.z * 0.5f + 0.5f));
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.NormalMap;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ---------------------------------------------------------------------
    // Seabed dressing, by zone
    // ---------------------------------------------------------------------

    static int DressSeabed(Terrain terrain)
    {
        GameObject root = Fresh("Seabed Props");
        root.isStatic = true;
        var rng = new System.Random(21);

        Mesh boulder = AssetDatabase.LoadAssetAtPath<Mesh>($"{SeabedFolder}/BrokenRock.asset");
        Material darkStone = AssetDatabase.LoadAssetAtPath<Material>($"{SeabedFolder}/RoughStone.mat");
        Material paleStone = AssetDatabase.LoadAssetAtPath<Material>($"{SeabedFolder}/PaleRockSurface.mat");
        GameObject slab = AssetDatabase.LoadAssetAtPath<GameObject>($"{SeabedFolder}/CoastRock.prefab");
        GameObject coral = AssetDatabase.LoadAssetAtPath<GameObject>($"{SeabedFolder}/DeepCoral.prefab");
        GameObject sponge = AssetDatabase.LoadAssetAtPath<GameObject>($"{SeabedFolder}/Sponge.prefab");

        int count = 0;
        // Midnight floor: dark boulders, cold-water corals and glass sponges on bare silt.
        count += Scatter(terrain, root, rng, 26, Band.Deep, (p, n, s) => Boulder(boulder, darkStone, p, n, s, 0.6f, 2.4f, rng));
        count += Scatter(terrain, root, rng, 16, Band.Deep, (p, n, s) => Place(coral, p, n, s, 0.6f, 1.4f, 0.4f, rng));
        count += Scatter(terrain, root, rng, 12, Band.Deep, (p, n, s) => Place(sponge, p, n, s, 0.5f, 1.1f, 0.3f, rng));
        // Twilight slope: rock slabs following the slope, boulders, corals and sponges on ledges.
        count += Scatter(terrain, root, rng, 30, Band.Slope, (p, n, s) => Place(slab, p, n, s, 1.5f, 4f, 0.9f, rng, solid: true));
        count += Scatter(terrain, root, rng, 26, Band.Slope, (p, n, s) => Boulder(boulder, darkStone, p, n, s, 0.6f, 2.2f, rng));
        count += Scatter(terrain, root, rng, 14, Band.Slope, (p, n, s) => Place(coral, p, n, s, 0.6f, 1.3f, 0.5f, rng));
        count += Scatter(terrain, root, rng, 14, Band.Slope, (p, n, s) => Place(sponge, p, n, s, 0.5f, 1f, 0.5f, rng));
        // Sunlit shelf: pale sun-bleached rocks among the coral garden and sand.
        count += Scatter(terrain, root, rng, 18, Band.Shelf, (p, n, s) => Boulder(boulder, paleStone, p, n, s, 0.4f, 1.5f, rng));
        count += Scatter(terrain, root, rng, 10, Band.Shelf, (p, n, s) => Place(sponge, p, n, s, 0.4f, 0.8f, 0.3f, rng));
        return count;
    }

    enum Band { Deep, Slope, Shelf }

    static int Scatter(Terrain terrain, GameObject root, System.Random rng, int wanted, Band band,
                       System.Func<Vector3, Vector3, float, GameObject> make)
    {
        TerrainData data = terrain.terrainData;
        Vector3 origin = terrain.transform.position;
        var startPatch = new Vector2(60f, 17.7f);
        wanted = Mathf.RoundToInt(wanted * data.size.x * data.size.z / (PlaySize * PlaySize)); // same density on a bigger terrain
        int placed = 0;
        for (int attempt = 0; attempt < wanted * 40 && placed < wanted; attempt++)
        {
            float u = 0.04f + (float)rng.NextDouble() * 0.92f, v = 0.04f + (float)rng.NextDouble() * 0.92f;
            float y = data.GetInterpolatedHeight(u, v);
            float steep = data.GetSteepness(u, v);
            Vector3 p = origin + new Vector3(u * data.size.x, y, v * data.size.z);
            if (Vector2.Distance(new Vector2(p.x, p.z), startPatch) < 7f) continue; // keep the shark's start clear

            Band here = y < 4f ? Band.Deep : y >= 12f ? Band.Shelf : Band.Slope;
            if (here != band || steep > 40f) continue;

            GameObject go = make(p, data.GetInterpolatedNormal(u, v), steep);
            if (go == null) continue;
            go.transform.SetParent(root.transform, true);
            placed++;
        }
        return placed;
    }

    static GameObject Boulder(Mesh mesh, Material material, Vector3 p, Vector3 normal, float steep,
                              float min, float max, System.Random rng)
    {
        if (mesh == null) return null;
        var go = new GameObject("Boulder");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        go.layer = TerrainLayer;
        go.isStatic = true;
        float s = Range(rng, min, max);
        go.transform.localScale = new Vector3(s * Range(rng, 0.8f, 1.3f), s * Range(rng, 0.5f, 0.9f), s * Range(rng, 0.8f, 1.3f));
        go.transform.rotation = Quaternion.FromToRotation(Vector3.up, Vector3.Slerp(Vector3.up, normal, 0.5f))
                              * Quaternion.Euler(0f, Range(rng, 0f, 360f), 0f);
        go.transform.position = p - Vector3.up * s * 0.25f; // partly buried
        return go;
    }

    static GameObject Place(GameObject prefab, Vector3 p, Vector3 normal, float steep, float min, float max,
                            float alignToSlope, System.Random rng, bool solid = false)
    {
        if (prefab == null) return null;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        float s = Range(rng, min, max);
        go.transform.localScale = Vector3.one * s;
        go.transform.rotation = Quaternion.FromToRotation(Vector3.up, Vector3.Slerp(Vector3.up, normal, alignToSlope))
                              * Quaternion.Euler(0f, Range(rng, 0f, 360f), 0f);
        go.transform.position = p - Vector3.up * 0.03f * s;
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.isStatic = true;
            if (solid) t.gameObject.layer = TerrainLayer;
        }
        return go;
    }

    static int AddJellyfish(Terrain terrain)
    {
        GameObject root = Fresh("Jellyfish");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Creatures/CrystalJelly.prefab");
        if (prefab == null) return 0;
        var rng = new System.Random(5);
        Vector3 size = terrain.terrainData.size;
        int placed = 0;
        for (int attempt = 0; attempt < 200 && placed < 16; attempt++)
        {
            var p = new Vector3(Range(rng, 10f, PlaySize - 10f), Range(rng, 7f, 16f), Range(rng, 10f, PlaySize - 10f));
            if (p.y < terrain.SampleHeight(p) + 3f) continue;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            go.transform.position = p;
            go.transform.localScale = Vector3.one * Range(rng, 1.2f, 2.2f);
            go.transform.rotation = Quaternion.Euler(0f, Range(rng, 0f, 360f), 0f);
            go.AddComponent<JellyDrift>();
            placed++;
        }
        return placed;
    }

    // ---------------------------------------------------------------------
    // HUD: minimap + depth gauge, top right
    // ---------------------------------------------------------------------

    static void BuildHud(Terrain terrain, Transform shark)
    {
        GameObject canvasGo = Fresh("HUD Canvas");
        canvasGo.layer = LayerMask.NameToLayer("UI");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        var panelColor = new Color(0.02f, 0.06f, 0.1f, 0.55f);
        RectTransform topRight = Rect("TopRight", canvasGo.transform, new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(336f, 310f));

        // Minimap
        RectTransform frame = Rect("MiniMap", topRight, new Vector2(1f, 1f), Vector2.zero, new Vector2(236f, 236f));
        Img(frame, panelColor);
        RectTransform mapRect = Rect("Map", frame, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 220f));
        var map = mapRect.gameObject.AddComponent<RawImage>();
        map.texture = MiniMapTexture(terrain);
        map.raycastTarget = false;
        RectTransform marker = Rect("SharkMarker", mapRect, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f));
        Img(marker, new Color(1f, 0.55f, 0.2f), ArrowSprite());
        Label("North", frame, "N", 16, new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(30f, 22f), TextAlignmentOptions.Center);

        // Depth gauge, to the left of the map
        RectTransform depthPanel = Rect("DepthGauge", topRight, new Vector2(1f, 1f), new Vector2(-248f, 0f), new Vector2(88f, 236f));
        Img(depthPanel, panelColor);
        RectTransform gauge = Rect("Bar", depthPanel, new Vector2(0f, 1f), new Vector2(14f, -10f), new Vector2(16f, 216f));
        var bar = gauge.gameObject.AddComponent<RawImage>();
        bar.texture = GaugeTexture();
        bar.raycastTarget = false;
        RectTransform depthMarker = Rect("Marker", gauge, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(30f, 5f));
        depthMarker.pivot = new Vector2(0.5f, 0.5f);
        Img(depthMarker, Color.white);
        string[] ticks = { "0 m", "200 m", "1000 m", "1800 m" };
        for (int i = 0; i < ticks.Length; i++)
        {
            TMP_Text tick = Label("Tick " + ticks[i], gauge, ticks[i], 13, new Vector2(1f, 1f), Vector2.zero, new Vector2(50f, 18f),
                                  TextAlignmentOptions.Left);
            tick.rectTransform.pivot = new Vector2(0f, 0.5f);
            tick.rectTransform.anchoredPosition = new Vector2(6f, -i / 3f * 216f);
        }

        // Readout under both panels
        TMP_Text depthText = Label("DepthText", topRight, "0 m", 34, new Vector2(1f, 1f), new Vector2(0f, -244f), new Vector2(336f, 40f),
                                   TextAlignmentOptions.Right);
        depthText.fontStyle = FontStyles.Bold;
        TMP_Text zoneText = Label("ZoneText", topRight, "SUNLIGHT ZONE", 18, new Vector2(1f, 1f), new Vector2(0f, -284f), new Vector2(336f, 24f),
                                  TextAlignmentOptions.Right);
        zoneText.characterSpacing = 4f;

        BuildInstructions(canvasGo.transform, panelColor);

        var hud = canvasGo.AddComponent<DepthHud>();
        var so = new SerializedObject(hud);
        so.FindProperty("shark").objectReferenceValue = shark;
        so.FindProperty("mapArea").rectValue = new Rect(0f, 0f, PlaySize, PlaySize);
        so.FindProperty("map").objectReferenceValue = mapRect;
        so.FindProperty("sharkMarker").objectReferenceValue = marker;
        so.FindProperty("gauge").objectReferenceValue = gauge;
        so.FindProperty("depthMarker").objectReferenceValue = depthMarker;
        so.FindProperty("depthText").objectReferenceValue = depthText;
        so.FindProperty("zoneText").objectReferenceValue = zoneText;
        so.ApplyModifiedProperties();
    }

    /// <summary>Start prompt (bottom centre), controls card (bottom left) and an "H" hint.</summary>
    static void BuildInstructions(Transform canvas, Color panelColor)
    {
        RectTransform prompt = Rect("StartPrompt", canvas, new Vector2(0.5f, 0f), new Vector2(0f, 110f), new Vector2(860f, 116f));
        Img(prompt, panelColor);
        var promptGroup = prompt.gameObject.AddComponent<CanvasGroup>();
        promptGroup.blocksRaycasts = false;
        TMP_Text title = Label("Title", prompt, "Rise from the midnight zone", 40, new Vector2(0.5f, 1f), new Vector2(0f, -12f),
                               new Vector2(840f, 52f), TextAlignmentOptions.Center);
        title.fontStyle = FontStyles.Bold;
        TMP_Text hint = Label("Hint", prompt, "Press SPACE to start", 24, new Vector2(0.5f, 0f), new Vector2(0f, 14f),
                              new Vector2(840f, 36f), TextAlignmentOptions.Center);
        hint.color = new Color(0.7f, 0.9f, 1f);

        RectTransform card = Rect("ControlsCard", canvas, new Vector2(0f, 0f), new Vector2(24f, 24f), new Vector2(400f, 330f));
        Img(card, panelColor);
        var cardGroup = card.gameObject.AddComponent<CanvasGroup>();
        cardGroup.blocksRaycasts = false;
        TMP_Text cardTitle = Label("Title", card, "CONTROLS", 18, new Vector2(0f, 1f), new Vector2(20f, -14f), new Vector2(360f, 26f),
                                   TextAlignmentOptions.Left);
        cardTitle.characterSpacing = 6f;
        cardTitle.color = new Color(0.45f, 0.8f, 1f);
        TMP_Text body = Label("Body", card, "", 20, new Vector2(0f, 1f), new Vector2(20f, -48f), new Vector2(370f, 270f),
                              TextAlignmentOptions.TopLeft);
        body.textWrappingMode = TextWrappingModes.Normal;
        body.lineSpacing = 8f;

        RectTransform hintBox = Rect("HelpHint", canvas, new Vector2(0f, 0f), new Vector2(24f, 24f), new Vector2(240f, 34f));
        Img(hintBox, panelColor);
        var hintGroup = hintBox.gameObject.AddComponent<CanvasGroup>();
        hintGroup.alpha = 0f;
        hintGroup.blocksRaycasts = false;
        Label("Text", hintBox, "<b>H</b>   Show controls", 18, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 30f),
              TextAlignmentOptions.Center);

        var help = canvas.gameObject.AddComponent<ControlsHelp>();
        var so = new SerializedObject(help);
        so.FindProperty("gameManager").objectReferenceValue = Object.FindAnyObjectByType<GameManager>();
        so.FindProperty("mouseControls").objectReferenceValue = Object.FindAnyObjectByType<TempMouseSwimControls>();
        so.FindProperty("startPrompt").objectReferenceValue = promptGroup;
        so.FindProperty("startTitle").objectReferenceValue = title;
        so.FindProperty("startHint").objectReferenceValue = hint;
        so.FindProperty("controlsCard").objectReferenceValue = cardGroup;
        so.FindProperty("controlsBody").objectReferenceValue = body;
        so.FindProperty("helpHint").objectReferenceValue = hintGroup;
        so.ApplyModifiedProperties();
    }

    /// <summary>Corner-anchored rect: anchor and pivot at the same point, then offset and size.</summary>
    static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.sizeDelta = size;
        rt.anchoredPosition = position;
        return rt;
    }

    static UnityEngine.UI.Image Img(RectTransform rt, Color color, Sprite sprite = null)
    {
        var image = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
        image.color = color;
        image.sprite = sprite;
        image.raycastTarget = false;
        return image;
    }

    static TMP_Text Label(string name, Transform parent, string text, float size, Vector2 anchor, Vector2 position, Vector2 box,
                          TextAlignmentOptions align)
    {
        RectTransform rt = Rect(name, parent, anchor, position, box);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.alignment = align;
        t.color = new Color(0.9f, 0.96f, 1f);
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        return t;
    }

    /// <summary>Map of the play area's seabed coloured by zone, with hill shading and 2 m contour lines.</summary>
    static Texture2D MiniMapTexture(Terrain terrain)
    {
        const int n = 256;
        TerrainData data = terrain.terrainData;
        var tex = new Texture2D(n, n, TextureFormat.RGB24, false);
        var light = new Vector3(-0.5f, 0.8f, 0.4f).normalized;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x / (n - 1f) * PlaySize - terrain.transform.position.x) / data.size.x;
            float v = (y / (n - 1f) * PlaySize - terrain.transform.position.z) / data.size.z;
            float h = data.GetInterpolatedHeight(u, v) + terrain.transform.position.y;
            Color c = h >= 12f ? Color.Lerp(new Color(0.42f, 0.72f, 0.70f), new Color(0.78f, 0.86f, 0.70f), Mathf.InverseLerp(12f, 17f, h))
                    : h >= 6f ? Color.Lerp(new Color(0.08f, 0.24f, 0.45f), new Color(0.2f, 0.5f, 0.62f), Mathf.InverseLerp(6f, 12f, h))
                    : Color.Lerp(new Color(0.02f, 0.05f, 0.16f), new Color(0.05f, 0.14f, 0.32f), Mathf.InverseLerp(0f, 6f, h));
            float shade = Mathf.Lerp(0.7f, 1.15f, Mathf.Clamp01(Vector3.Dot(data.GetInterpolatedNormal(u, v), light)));
            float contour = Mathf.Abs(h / 2f - Mathf.Round(h / 2f)) < 0.04f ? 0.75f : 1f;
            tex.SetPixel(x, y, c * shade * contour);
        }
        return SavePng(tex, $"{UiFolder}/MiniMap.png", sprite: false);
    }

    static Texture2D GaugeTexture()
    {
        var tex = new Texture2D(4, 192, TextureFormat.RGB24, false);
        for (int y = 0; y < 192; y++)
        {
            float d = 1f - y / 191f; // texture y=0 is the bottom
            Color c = d < 1f / 3f ? Color.Lerp(new Color(0.55f, 0.85f, 0.85f), new Color(0.12f, 0.45f, 0.65f), d * 3f)
                    : d < 2f / 3f ? Color.Lerp(new Color(0.08f, 0.25f, 0.5f), new Color(0.04f, 0.1f, 0.28f), d * 3f - 1f)
                    : Color.Lerp(new Color(0.03f, 0.05f, 0.16f), new Color(0.01f, 0.01f, 0.04f), d * 3f - 2f);
            for (int x = 0; x < 4; x++) tex.SetPixel(x, y, c);
        }
        return SavePng(tex, $"{UiFolder}/DepthGauge.png", sprite: false);
    }

    static Sprite ArrowSprite()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            // Arrow pointing up: wide at the bottom with a notch, tip at the top.
            float px = (x - n / 2f) / (n / 2f), py = y / (float)n;
            float halfWidth = (1f - py) * 0.85f;
            bool inside = Mathf.Abs(px) < halfWidth && py > 0.08f + Mathf.Abs(px) * 0.35f;
            tex.SetPixel(x, y, inside ? Color.white : Color.clear);
        }
        SavePng(tex, $"{UiFolder}/SharkArrow.png", sprite: true);
        return AssetDatabase.LoadAssetAtPath<Sprite>($"{UiFolder}/SharkArrow.png");
    }

    static Texture2D SavePng(Texture2D tex, string path, bool sprite)
    {
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    static void MakeTransparent(Material m, bool additive)
    {
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", additive ? 2f : 0f);
        m.SetFloat("_ZWrite", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    /// <summary>Deletes a root object with this name (from a previous run) and creates an empty one.</summary>
    static GameObject Fresh(string name)
    {
        foreach (GameObject old in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
                     .Where(t => t.parent == null && t.name == name).Select(t => t.gameObject).ToArray())
            Object.DestroyImmediate(old);
        return new GameObject(name);
    }

    static float Range(System.Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);

    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        Folder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
