using UnityEngine;

/// <summary>
/// Makes the water look different in each ocean zone, based on the camera's depth:
///   Sunlight : clear turquoise water, bright sun with moving caustics, little marine snow
///   Twilight : deep blue, dim and hazy, sunlight fading out
///   Midnight : nearly black; only bioluminescence, a soft light near the camera and drifting marine snow
/// Each zone's look is set in the Inspector; values blend smoothly between zones.
/// Works alongside AscentAtmosphere (which blends the post-processing volumes).
/// </summary>
public class UnderwaterAtmosphere : MonoBehaviour
{
    [System.Serializable]
    public struct ZoneLook
    {
        public string name;
        [Tooltip("Colour of the water in the distance (fog).")]
        public Color waterColor;
        [Tooltip("Exponential-squared fog density. Higher = shorter visibility.")]
        public float fogDensity;
        [Tooltip("Flat ambient light on everything.")]
        public Color ambient;
        [Tooltip("Multiplier on the sun / caustic lights.")]
        [Range(0f, 1f)] public float sunlight;
        [Tooltip("Brightness of the 360 reef photo behind everything.")]
        [Range(0f, 1f)] public float backdrop;
        [Tooltip("Marine snow particles per second.")]
        public float marineSnow;
        [Tooltip("Strength of sky reflections on shiny surfaces. Underwater there is little to reflect.")]
        [Range(0f, 1f)] public float reflections;
    }

    [Tooltip("Leave empty to use the main camera.")]
    [SerializeField] private Transform viewer;

    [SerializeField] private ZoneLook sunlightZone = new ZoneLook
    {
        name = "Sunlight", waterColor = new Color(0.10f, 0.42f, 0.50f), fogDensity = 0.035f,
        ambient = new Color(0.36f, 0.52f, 0.56f), sunlight = 1f, backdrop = 0.9f, marineSnow = 8f, reflections = 0.3f,
    };
    [SerializeField] private ZoneLook twilightZone = new ZoneLook
    {
        name = "Twilight", waterColor = new Color(0.02f, 0.10f, 0.22f), fogDensity = 0.055f,
        ambient = new Color(0.07f, 0.13f, 0.22f), sunlight = 0.25f, backdrop = 0.2f, marineSnow = 30f, reflections = 0.08f,
    };
    [SerializeField] private ZoneLook midnightZone = new ZoneLook
    {
        name = "Midnight", waterColor = new Color(0.004f, 0.012f, 0.03f), fogDensity = 0.07f,
        ambient = new Color(0.03f, 0.04f, 0.06f), sunlight = 0f, backdrop = 0f, marineSnow = 70f, reflections = 0f,
    };

    [Header("Scene objects (all optional)")]
    [Tooltip("Sun lights with caustic cookies. Their Inspector intensity is the Sunlight-zone intensity.")]
    [SerializeField] private Light[] sunLights;
    [Tooltip("The 360 reef photo sphere.")]
    [SerializeField] private Renderer backdrop;
    [Tooltip("Soft light that follows the camera so the deep is not pitch black.")]
    [SerializeField] private Light viewerLight;
    [SerializeField] private float viewerLightDeep = 2.5f;
    [SerializeField] private ParticleSystem marineSnow;

    private float[] baseIntensity;
    private MaterialPropertyBlock block;
    private static readonly int BaseColorFactor = Shader.PropertyToID("baseColorFactor");

    /// <summary>The blended look at the viewer's current depth.</summary>
    public ZoneLook Current { get; private set; }

    private void Start()
    {
        if (viewer == null && Camera.main != null) viewer = Camera.main.transform;

        baseIntensity = new float[sunLights != null ? sunLights.Length : 0];
        for (int i = 0; i < baseIntensity.Length; i++)
            if (sunLights[i] != null) baseIntensity[i] = sunLights[i].intensity;

        block = new MaterialPropertyBlock();
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
    }

    private void LateUpdate()
    {
        if (viewer == null) return;

        ZoneLook look = Evaluate(viewer.position.y);
        Current = look;

        RenderSettings.fogColor = look.waterColor;
        RenderSettings.fogDensity = look.fogDensity;
        RenderSettings.ambientLight = look.ambient;
        RenderSettings.reflectionIntensity = look.reflections;

        for (int i = 0; i < baseIntensity.Length; i++)
            if (sunLights[i] != null) sunLights[i].intensity = baseIntensity[i] * look.sunlight;

        if (backdrop != null)
        {
            backdrop.GetPropertyBlock(block);
            block.SetColor(BaseColorFactor, Color.Lerp(look.waterColor, Color.white, look.backdrop));
            backdrop.SetPropertyBlock(block);
        }

        if (viewerLight != null)
            viewerLight.intensity = viewerLightDeep * (1f - look.sunlight);

        if (marineSnow != null)
        {
            ParticleSystem.EmissionModule emission = marineSnow.emission;
            emission.rateOverTime = look.marineSnow;
        }
    }

    /// <summary>Blends the three zone looks; each zone's look is exact at the middle of its third.</summary>
    private ZoneLook Evaluate(float worldY)
    {
        float t = Mathf.Clamp(OceanZones.DepthFraction(worldY) * 3f - 0.5f, 0f, 2f);
        return t < 1f ? Lerp(sunlightZone, twilightZone, Smooth(t)) : Lerp(twilightZone, midnightZone, Smooth(t - 1f));
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static ZoneLook Lerp(ZoneLook a, ZoneLook b, float t) => new ZoneLook
    {
        name = t < 0.5f ? a.name : b.name,
        waterColor = Color.Lerp(a.waterColor, b.waterColor, t),
        fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t),
        ambient = Color.Lerp(a.ambient, b.ambient, t),
        sunlight = Mathf.Lerp(a.sunlight, b.sunlight, t),
        backdrop = Mathf.Lerp(a.backdrop, b.backdrop, t),
        marineSnow = Mathf.Lerp(a.marineSnow, b.marineSnow, t),
        reflections = Mathf.Lerp(a.reflections, b.reflections, t),
    };
}
