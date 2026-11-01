using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Builds the top-right HUD (minimap + depth meter) in the open scene and wires it to DepthHud.
/// Extracted from the 'ted' branch's UnderwaterSceneBuilder, but sized from THIS scene:
///   - minimap area  = SharkBoundary's minX/maxX/minZ/maxZ (falls back to the terrain footprint)
///   - minimap shading/zone colours = OceanZones.SeabedY .. OceanZones.SurfaceY
/// Needs in the open scene: a Terrain, a SharkRise, (ideally) a SharkBoundary.
/// Needs in the project: OceanZones.cs and DepthHud.cs, and TextMesh Pro essentials imported.
///
/// Menu: SwitchShark > Build HUD (minimap + depth meter)
/// Safe to re-run: it deletes the old "HUD Canvas" and rebakes the minimap. Hand edits to that
/// canvas are lost, so save it as a prefab (or rename it) before customising by hand.
/// </summary>
public static class HudBuilder
{
    const string UiFolder = "Assets/UI/HUD";
    const int MapResolution = 256;
    const float ContourInterval = 5f; // metres between contour lines on the minimap

    static readonly Color PanelColor = new Color(0.02f, 0.06f, 0.1f, 0.55f);
    static readonly Color TextColor = new Color(0.9f, 0.96f, 1f);

    [MenuItem("SwitchShark/Build HUD (minimap + depth meter)")]
    public static void BuildMenu()
    {
        Terrain terrain = Object.FindAnyObjectByType<Terrain>();
        SharkRise shark = Object.FindAnyObjectByType<SharkRise>();
        if (terrain == null) { Debug.LogError("[HudBuilder] No Terrain in the open scene."); return; }
        if (shark == null) { Debug.LogError("[HudBuilder] No SharkRise in the open scene."); return; }

        EnsureFolder("Assets/UI");
        EnsureFolder(UiFolder);

        Rect area = PlayArea(terrain);
        BuildHud(terrain, shark, area);

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[HudBuilder] HUD built. Minimap covers X {area.xMin:F0}..{area.xMax:F0}, Z {area.yMin:F0}..{area.yMax:F0}; " +
                  $"depth gauge runs Y {OceanZones.SeabedY:F0}..{OceanZones.SurfaceY:F0}.");
    }

    // ---------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------

    static void BuildHud(Terrain terrain, SharkRise shark, Rect area)
    {
        GameObject canvasGo = Fresh("HUD Canvas");
        Undo.RegisterCreatedObjectUndo(canvasGo, "Build HUD");
        canvasGo.layer = LayerMask.NameToLayer("UI");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Everything sits in one block anchored to the top-right corner, 24 px from the edges.
        RectTransform topRight = MakeRect("TopRight", canvasGo.transform, new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(336f, 310f));

        // ---- Minimap ----
        RectTransform frame = MakeRect("MiniMap", topRight, new Vector2(1f, 1f), Vector2.zero, new Vector2(236f, 236f));
        Img(frame, PanelColor);
        RectTransform mapRect = MakeRect("Map", frame, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 220f));
        var map = mapRect.gameObject.AddComponent<RawImage>();
        map.texture = MiniMapTexture(terrain, area);
        map.raycastTarget = false;
        RectTransform marker = MakeRect("SharkMarker", mapRect, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f));
        Img(marker, new Color(1f, 0.55f, 0.2f), ArrowSprite());
        Label("North", frame, "N", 16, new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(30f, 22f), TextAlignmentOptions.Center);

        // ---- Depth gauge, to the left of the map ----
        RectTransform depthPanel = MakeRect("DepthGauge", topRight, new Vector2(1f, 1f), new Vector2(-248f, 0f), new Vector2(88f, 236f));
        Img(depthPanel, PanelColor);
        RectTransform gauge = MakeRect("Bar", depthPanel, new Vector2(0f, 1f), new Vector2(14f, -10f), new Vector2(16f, 216f));
        var bar = gauge.gameObject.AddComponent<RawImage>();
        bar.texture = GaugeTexture();
        bar.raycastTarget = false;
        RectTransform depthMarker = MakeRect("Marker", gauge, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(30f, 5f));
        depthMarker.pivot = new Vector2(0.5f, 0.5f);
        Img(depthMarker, Color.white);

        // Tick labels are real-ocean depths at the zone boundaries (the three zones are equal thirds of the gauge).
        string[] ticks = { "0 m", "200 m", "1000 m", "1800 m" };
        for (int i = 0; i < ticks.Length; i++)
        {
            TMP_Text tick = Label("Tick " + ticks[i], gauge, ticks[i], 13, new Vector2(1f, 1f), Vector2.zero, new Vector2(50f, 18f),
                                  TextAlignmentOptions.Left);
            tick.rectTransform.pivot = new Vector2(0f, 0.5f);
            tick.rectTransform.anchoredPosition = new Vector2(6f, -i / 3f * 216f);
        }

        // ---- Readout under both panels ----
        TMP_Text depthText = Label("DepthText", topRight, "0 m", 34, new Vector2(1f, 1f), new Vector2(0f, -244f), new Vector2(336f, 40f),
                                   TextAlignmentOptions.Right);
        depthText.fontStyle = FontStyles.Bold;
        TMP_Text zoneText = Label("ZoneText", topRight, "SUNLIGHT ZONE", 18, new Vector2(1f, 1f), new Vector2(0f, -284f), new Vector2(336f, 24f),
                                  TextAlignmentOptions.Right);
        zoneText.characterSpacing = 4f;

        // ---- Wire up DepthHud ----
        var hud = canvasGo.AddComponent<DepthHud>();
        var so = new SerializedObject(hud);
        so.FindProperty("shark").objectReferenceValue = shark.transform;
        so.FindProperty("mapArea").rectValue = area;
        so.FindProperty("map").objectReferenceValue = mapRect;
        so.FindProperty("sharkMarker").objectReferenceValue = marker;
        so.FindProperty("gauge").objectReferenceValue = gauge;
        so.FindProperty("depthMarker").objectReferenceValue = depthMarker;
        so.FindProperty("depthText").objectReferenceValue = depthText;
        so.FindProperty("zoneText").objectReferenceValue = zoneText;
        so.ApplyModifiedProperties();
    }

    /// <summary>World X/Z rectangle the minimap shows: SharkBoundary's limits, else the terrain's footprint.</summary>
    static Rect PlayArea(Terrain terrain)
    {
        var boundary = Object.FindAnyObjectByType<SharkBoundary>();
        if (boundary != null)
        {
            var so = new SerializedObject(boundary);
            SerializedProperty minX = so.FindProperty("minX"), maxX = so.FindProperty("maxX");
            SerializedProperty minZ = so.FindProperty("minZ"), maxZ = so.FindProperty("maxZ");
            if (minX != null && maxX != null && minZ != null && maxZ != null &&
                maxX.floatValue > minX.floatValue && maxZ.floatValue > minZ.floatValue)
                return Rect.MinMaxRect(minX.floatValue, minZ.floatValue, maxX.floatValue, maxZ.floatValue);

            Debug.LogWarning("[HudBuilder] Couldn't read SharkBoundary's limits; using the terrain footprint instead.");
        }

        Vector3 p = terrain.transform.position;
        Vector3 s = terrain.terrainData.size;
        return new Rect(p.x, p.z, s.x, s.z);
    }

    // ---------------------------------------------------------------------
    // Generated textures
    // ---------------------------------------------------------------------

    /// <summary>Top-down map of the seabed coloured by zone (thirds of SeabedY..SurfaceY), with hill shading and contour lines.</summary>
    static Texture2D MiniMapTexture(Terrain terrain, Rect area)
    {
        const int n = MapResolution;
        TerrainData data = terrain.terrainData;
        Vector3 origin = terrain.transform.position;
        var tex = new Texture2D(n, n, TextureFormat.RGB24, false);
        var light = new Vector3(-0.5f, 0.8f, 0.4f).normalized;

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float worldX = area.xMin + x / (n - 1f) * area.width;
            float worldZ = area.yMin + y / (n - 1f) * area.height;
            float u = Mathf.Clamp01((worldX - origin.x) / data.size.x);
            float v = Mathf.Clamp01((worldZ - origin.z) / data.size.z);
            float h = data.GetInterpolatedHeight(u, v) + origin.y;

            // 0 = at the deepest point of the gauge, 1 = at the surface.
            float f = Mathf.InverseLerp(OceanZones.SeabedY, OceanZones.SurfaceY, h);
            Color c = f >= 2f / 3f ? Color.Lerp(new Color(0.42f, 0.72f, 0.70f), new Color(0.78f, 0.86f, 0.70f), Mathf.InverseLerp(2f / 3f, 1f, f))
                    : f >= 1f / 3f ? Color.Lerp(new Color(0.08f, 0.24f, 0.45f), new Color(0.2f, 0.5f, 0.62f), Mathf.InverseLerp(1f / 3f, 2f / 3f, f))
                    : Color.Lerp(new Color(0.02f, 0.05f, 0.16f), new Color(0.05f, 0.14f, 0.32f), Mathf.InverseLerp(0f, 1f / 3f, f));

            float shade = Mathf.Lerp(0.7f, 1.15f, Mathf.Clamp01(Vector3.Dot(data.GetInterpolatedNormal(u, v), light)));
            float contour = Mathf.Abs(h / ContourInterval - Mathf.Round(h / ContourInterval)) < 0.04f ? 0.75f : 1f;
            tex.SetPixel(x, y, c * shade * contour);
        }
        return SavePng(tex, $"{UiFolder}/MiniMap.png", sprite: false);
    }

    /// <summary>Vertical gradient for the depth bar: light at the top (surface) to near-black at the bottom.</summary>
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

    /// <summary>White arrow pointing up (north). DepthHud rotates it to match the shark's heading.</summary>
    static Sprite ArrowSprite()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
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
    // UI helpers
    // ---------------------------------------------------------------------

    static RectTransform MakeRect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor; // anchor, pivot and position all refer to the same corner/point
        rt.sizeDelta = size;
        rt.anchoredPosition = position;
        return rt;
    }

    static Image Img(RectTransform rt, Color color, Sprite sprite = null)
    {
        var image = rt.gameObject.AddComponent<Image>();
        image.color = color;
        image.sprite = sprite;
        image.raycastTarget = false;
        return image;
    }

    static TMP_Text Label(string name, Transform parent, string text, float size, Vector2 anchor, Vector2 position, Vector2 box,
                          TextAlignmentOptions align)
    {
        RectTransform rt = MakeRect(name, parent, anchor, position, box);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.alignment = align;
        t.color = TextColor;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        return t;
    }

    /// <summary>Deletes a root object with this name (from a previous run) and creates an empty one.</summary>
    static GameObject Fresh(string name)
    {
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            if (root.name == name) Object.DestroyImmediate(root);
        return new GameObject(name);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
