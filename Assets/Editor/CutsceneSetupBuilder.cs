using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// Builds the opening + ending cutscenes in the open scene, ready to play:
///
///   OPENING (about 10 s): slow push-in on the resting shark, a "BENEATH THE SURFACE" title card
///                         fades in and out, then the camera swings round behind the shark.
///   ENDING  (about 9 s) : the camera pulls back and up from the shark as it glides at the
///                         surface, and an end card lists what it ate.
///
/// What it creates:
///   - "Cutscenes" root: CutsceneCam (inactive CinemachineCamera), OpeningCutscene, EndingCutscene
///   - "CutsceneMarkers" parented to the shark: the camera start/end poses (move them to taste!)
///   - "Cutscene Canvas": title card + end card (with the EndCard script)
///   - wires GameManager's Opening Cutscene / Ending Cutscene fields
///
/// Needs in the open scene: SharkRise, GameManager (patched version with the cutscene fields),
/// SharkEater. Needs in the project: Cutscene.cs, ShotCutscene.cs, EndCard.cs and TextMesh Pro
/// essentials imported.
///
/// Menu: SwitchShark > Create Cutscene Setup (opening + ending)
/// Safe to re-run: it deletes and rebuilds everything it made (so hand edits to those objects are
/// lost - duplicate them first if you want to keep a tweaked version).
/// </summary>
public static class CutsceneSetupBuilder
{
    static readonly Color PanelColor = new Color(0.02f, 0.06f, 0.1f, 0.72f);
    static readonly Color BandColor = new Color(0f, 0.03f, 0.08f, 0.55f);
    static readonly Color TextColor = new Color(0.9f, 0.96f, 1f);

    [MenuItem("SwitchShark/Create Cutscene Setup (opening + ending)")]
    public static void Build()
    {
        SharkRise shark = Object.FindAnyObjectByType<SharkRise>();
        GameManager gameManager = Object.FindAnyObjectByType<GameManager>();
        SharkEater eater = Object.FindAnyObjectByType<SharkEater>();

        if (shark == null) { Debug.LogError("[CutsceneSetup] No SharkRise in the open scene."); return; }
        if (gameManager == null) Debug.LogWarning("[CutsceneSetup] No GameManager found - cutscenes will be built but not wired.");
        if (eater == null) Debug.LogWarning("[CutsceneSetup] No SharkEater found - the end card won't be able to show stats.");

        Transform sharkT = shark.transform;
        Bounds bounds = SharkBounds(sharkT, out bool found);
        float L = found ? Mathf.Clamp(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z), 0.2f, 60f) : 2f;
        Vector3 center = found ? bounds.center : sharkT.position;
        if (!found) Debug.LogWarning("[CutsceneSetup] Couldn't measure the shark; assuming it is about 2 units long. Adjust the markers by hand.");

        Vector3 forward = Vector3.ProjectOnPlane(sharkT.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3 up = Vector3.up;

        // ---- Markers (children of the shark, so shots follow it) ----
        Transform oldMarkers = sharkT.Find("CutsceneMarkers");
        if (oldMarkers != null) Object.DestroyImmediate(oldMarkers.gameObject);

        var markerGroup = new GameObject("CutsceneMarkers").transform;
        markerGroup.SetPositionAndRotation(center, sharkT.rotation);
        markerGroup.SetParent(sharkT, true);

        Transform look = Marker("LookTarget", markerGroup, center, center + forward);
        Transform openA = Marker("Opening_A (wide)", markerGroup, center - forward * 3.4f * L + right * 1.6f * L + up * 1.2f * L, center);
        Transform openB = Marker("Opening_B (close)", markerGroup, center - forward * 1.5f * L + right * 0.7f * L + up * 0.3f * L, center);
        Transform openC = Marker("Opening_C (behind, like the Rise camera)", markerGroup, center - forward * 2.2f * L + up * 0.9f * L, center + forward * 2f * L);
        Transform endA = Marker("Ending_A (close)", markerGroup, center - forward * 1.8f * L + right * 1.0f * L + up * 0.2f * L, center);
        Transform endB = Marker("Ending_B (pulled back)", markerGroup, center - forward * 5.5f * L + right * 2.8f * L + up * 3.2f * L, center);

        // ---- Cutscene objects ----
        GameObject root = Fresh("Cutscenes");

        var camGo = new GameObject("CutsceneCam");
        camGo.transform.SetParent(root.transform, false);
        camGo.transform.SetPositionAndRotation(openA.position, openA.rotation);
        Type cinemachineCamera = Type.GetType("Unity.Cinemachine.CinemachineCamera, Unity.Cinemachine");
        if (cinemachineCamera != null)
            camGo.AddComponent(cinemachineCamera);
        else
            Debug.LogWarning("[CutsceneSetup] Couldn't find Unity.Cinemachine.CinemachineCamera. Add a CinemachineCamera component to 'CutsceneCam' yourself (leave Tracking Target empty).");
        camGo.SetActive(false); // switched on by the cutscene when it starts

        // ---- UI cards ----
        GameObject canvasGo = Fresh("Cutscene Canvas");
        canvasGo.layer = LayerMask.NameToLayer("UI");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20; // above the HUD (10)
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Title card
        RectTransform titleRoot = Stretch("TitleCard", canvasGo.transform);
        CanvasGroup titleGroup = MakeCard(titleRoot);
        RectTransform band = MakeRect("Band", titleRoot, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, 260f));
        band.anchorMin = new Vector2(0f, 0.5f);
        band.anchorMax = new Vector2(1f, 0.5f);
        band.sizeDelta = new Vector2(0f, 260f);
        Img(band, BandColor);
        TMP_Text title = Label("Title", titleRoot, "BENEATH THE SURFACE", 96, new Vector2(0.5f, 0.5f), new Vector2(0f, 36f), new Vector2(1700f, 130f), TextAlignmentOptions.Center);
        title.fontStyle = FontStyles.Bold;
        title.characterSpacing = 12f;
        TMP_Text subtitle = Label("Subtitle", titleRoot, "RISE WITH THE LANTERN SHARK", 36, new Vector2(0.5f, 0.5f), new Vector2(0f, -62f), new Vector2(1400f, 56f), TextAlignmentOptions.Center);
        subtitle.characterSpacing = 8f;
        subtitle.color = new Color(TextColor.r, TextColor.g, TextColor.b, 0.8f);

        // End card
        RectTransform endRoot = Stretch("EndCard", canvasGo.transform);
        CanvasGroup endGroup = MakeCard(endRoot);
        RectTransform panel = MakeRect("Panel", endRoot, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 560f));
        Img(panel, PanelColor);
        TMP_Text headline = Label("Headline", panel, "YOU MADE IT", 72, new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(700f, 90f), TextAlignmentOptions.Center);
        headline.fontStyle = FontStyles.Bold;
        headline.characterSpacing = 8f;
        TMP_Text stats = Label("Stats", panel, "Fish eaten: 0", 40, new Vector2(0.5f, 1f), new Vector2(0f, -160f), new Vector2(640f, 360f), TextAlignmentOptions.TopLeft);
        stats.textWrappingMode = TextWrappingModes.Normal;

        var endCard = endRoot.gameObject.AddComponent<EndCard>();
        var endCardSo = new SerializedObject(endCard);
        SetRef(endCardSo, "gameManager", gameManager);
        SetRef(endCardSo, "eater", eater);
        SetRef(endCardSo, "stats", stats);
        endCardSo.ApplyModifiedProperties();

        // ---- Opening cutscene ----
        var openingGo = new GameObject("OpeningCutscene");
        openingGo.transform.SetParent(root.transform, false);
        var opening = openingGo.AddComponent<ShotCutscene>();
        opening.Configure(
            camGo,
            new List<ShotCutscene.Shot>
            {
                new ShotCutscene.Shot { name = "Push in on the shark", from = openA, to = openB, duration = 6f,   ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f), lookAt = look },
                new ShotCutscene.Shot { name = "Swing round behind",   from = openB, to = openC, duration = 3.5f, ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f), lookAt = look },
            },
            new List<ShotCutscene.Card>
            {
                new ShotCutscene.Card { name = "Title", group = titleGroup, startTime = 2.5f, fadeIn = 1.2f, hold = 3f, fadeOut = 1.2f },
            },
            0.5f);
        EditorUtility.SetDirty(opening);

        // ---- Ending cutscene ----
        var endingGo = new GameObject("EndingCutscene");
        endingGo.transform.SetParent(root.transform, false);
        var ending = endingGo.AddComponent<ShotCutscene>();
        ending.Configure(
            camGo,
            new List<ShotCutscene.Shot>
            {
                new ShotCutscene.Shot { name = "Pull back from the surface", from = endA, to = endB, duration = 8.5f, ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f), lookAt = look },
            },
            new List<ShotCutscene.Card>
            {
                new ShotCutscene.Card { name = "End card", group = endGroup, startTime = 3f, fadeIn = 1f, hold = 3.7f, fadeOut = 0.8f },
            },
            0.5f);
        EditorUtility.SetDirty(ending);

        // ---- Wire GameManager ----
        if (gameManager != null)
        {
            var gmSo = new SerializedObject(gameManager);
            SerializedProperty openingProp = gmSo.FindProperty("openingCutscene");
            SerializedProperty endingProp = gmSo.FindProperty("endingCutscene");
            if (openingProp == null || endingProp == null)
            {
                Debug.LogWarning("[CutsceneSetup] GameManager has no cutscene fields. Replace GameManager.cs with the patched version, then run this again (or drag OpeningCutscene / EndingCutscene into it by hand).");
            }
            else
            {
                openingProp.objectReferenceValue = opening;
                endingProp.objectReferenceValue = ending;
                gmSo.ApplyModifiedProperties();
            }
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = root;
        Debug.Log("[CutsceneSetup] Done. Select the markers under the shark (CutsceneMarkers) to adjust camera poses, " +
                  "and use 'Play On Start' on OpeningCutscene / EndingCutscene to preview a cutscene on its own.");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    static Bounds SharkBounds(Transform shark, out bool found)
    {
        found = false;
        Bounds bounds = new Bounds(shark.position, Vector3.zero);
        foreach (Renderer r in shark.GetComponentsInChildren<Renderer>())
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
            if (!found) { bounds = r.bounds; found = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return bounds;
    }

    static Transform Marker(string name, Transform parent, Vector3 position, Vector3 lookAtWorld)
    {
        var go = new GameObject(name);
        Vector3 direction = lookAtWorld - position;
        Quaternion rotation = direction.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(direction) : Quaternion.identity;
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.SetParent(parent, true);

        // A coloured label so the marker is easy to spot and drag in the Scene view.
        Texture2D icon = EditorGUIUtility.IconContent("sv_label_3").image as Texture2D;
        if (icon != null) EditorGUIUtility.SetIconForObject(go, icon);
        return go.transform;
    }

    static void SetRef(SerializedObject so, string field, Object value)
    {
        SerializedProperty p = so.FindProperty(field);
        if (p != null) p.objectReferenceValue = value;
        else Debug.LogWarning($"[CutsceneSetup] Couldn't find the field '{field}' to fill in.");
    }

    static CanvasGroup MakeCard(RectTransform root)
    {
        var group = root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        return group;
    }

    static RectTransform Stretch(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    static RectTransform MakeRect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor; // anchor, pivot and position all refer to the same point
        rt.sizeDelta = size;
        rt.anchoredPosition = position;
        return rt;
    }

    static Image Img(RectTransform rt, Color color)
    {
        var image = rt.gameObject.AddComponent<Image>();
        image.color = color;
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
}
