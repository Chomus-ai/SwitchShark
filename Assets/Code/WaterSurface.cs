using UnityEngine;

/// <summary>
/// Animates the underside of the sea surface: two scrolling ripple normal maps, so the
/// surface shimmers when you look up from the sunlight zone.
/// Put on the surface plane; its material should be URP Lit with a normal map and a detail normal map.
/// </summary>
public class WaterSurface : MonoBehaviour
{
    [SerializeField] private Vector2 rippleSpeed = new Vector2(0.02f, 0.012f);
    [SerializeField] private Vector2 detailSpeed = new Vector2(-0.031f, 0.017f);

    private Material material;
    private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
    private static readonly int DetailNormalMap = Shader.PropertyToID("_DetailNormalMap");

    private void Start()
    {
        var r = GetComponent<Renderer>();
        if (r != null) material = r.material; // instance, so the asset isn't modified in play mode
    }

    private void Update()
    {
        if (material == null) return;
        material.SetTextureOffset(BumpMap, rippleSpeed * Time.time);
        material.SetTextureOffset(DetailNormalMap, detailSpeed * Time.time);
    }
}
