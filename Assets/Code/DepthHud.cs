using TMPro;
using UnityEngine;

/// <summary>
/// Top-right HUD: a minimap of the seabed with the shark's position and heading, and a
/// depth gauge showing the current ocean zone and the real ocean depth it stands for.
/// The Canvas and its elements are built by SwitchShark > Build Underwater Scene.
/// </summary>
public class DepthHud : MonoBehaviour
{
    [SerializeField] private Transform shark;
    [SerializeField] private Terrain terrain;

    [Header("Minimap")]
    [SerializeField] private RectTransform map;
    [SerializeField] private RectTransform sharkMarker;

    [Header("Depth gauge")]
    [SerializeField] private RectTransform gauge;
    [SerializeField] private RectTransform depthMarker;
    [SerializeField] private TMP_Text depthText;
    [SerializeField] private TMP_Text zoneText;

    [Header("Zone label colours")]
    [SerializeField] private Color sunlightColor = new Color(1f, 0.86f, 0.45f);
    [SerializeField] private Color twilightColor = new Color(0.45f, 0.75f, 1f);
    [SerializeField] private Color midnightColor = new Color(0.7f, 0.55f, 1f);

    private void LateUpdate()
    {
        if (shark == null) return;
        Vector3 p = shark.position;

        if (map != null && sharkMarker != null && terrain != null)
        {
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            float u = Mathf.Clamp01((p.x - origin.x) / size.x);
            float v = Mathf.Clamp01((p.z - origin.z) / size.z);
            sharkMarker.anchoredPosition = new Vector2((u - 0.5f) * map.rect.width, (v - 0.5f) * map.rect.height);
            sharkMarker.localEulerAngles = new Vector3(0f, 0f, -shark.eulerAngles.y);
        }

        float fraction = OceanZones.DepthFraction(p.y);
        if (gauge != null && depthMarker != null)
            depthMarker.anchoredPosition = new Vector2(depthMarker.anchoredPosition.x, -fraction * gauge.rect.height);

        OceanZones.Zone zone = OceanZones.ZoneAt(p.y);
        if (depthText != null)
            depthText.text = $"{OceanZones.OceanDepth(p.y):N0} m";
        if (zoneText != null)
        {
            zoneText.text = OceanZones.Name(zone);
            zoneText.color = zone == OceanZones.Zone.Sunlight ? sunlightColor
                           : zone == OceanZones.Zone.Twilight ? twilightColor : midnightColor;
        }
    }
}
