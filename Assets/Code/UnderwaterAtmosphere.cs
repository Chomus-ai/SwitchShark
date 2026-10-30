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
        [Tooltip("Brightness of the 360 reef photo behind everything. 0 = hidden: distant water fades into the water colour, as in real water.")]
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
        name = "Sunlight", waterColor = new Color(0.12f, 0.46f, 0.54f), fogDensity = 0.03f,
        ambient = new Color(0.46f, 0.62f, 0.66f), sunlight = 1f, backdrop = 0f, marineSnow = 8f, reflections = 0.3f,
    };
    [SerializeField] private ZoneLook twilightZone = new ZoneLook
    {
        name = "Twilight", waterColor = new Color(0.04f, 0.16f, 0.32f), fogDensity = 0.04f,
        ambient = new Color(0.24f, 0.32f, 0.44f), sunlight = 0.4f, backdrop = 0f, marineSnow = 30f, reflections = 0.08f,
    };
    [SerializeField] private ZoneLook midnightZone = new ZoneLook
    {
        name = "Midnight", waterColor = new Color(0.012f, 0.03f, 0.07f), fogDensity = 0.045f,
        ambient = new Color(0.15f, 0.19f, 0.27f), sunlight = 0f, backdrop = 0f, marineSnow = 70f, reflections = 0f,
    };

    [Header("Scene objects (all optional)")]
    [Tooltip("Sun lights with caustic cookies. Their Inspector intensity is the Sunlight-zone intensity.")]
    [SerializeField] private Light[] sunLights;
    [Tooltip("The 360 reef photo sphere.")]
    [SerializeField] private Renderer backdrop;
    [Tooltip("Soft light that follows the camera so the deep is not pitch black.")]
    [SerializeField] private Light viewerLight;
    [SerializeField] private float viewerLightDeep = 9f;
    [SerializeField] private ParticleSystem marineSnow;
    [Tooltip("Underwater background (SwitchShark/Underwater Sky). Its horizon is kept equal to the fog colour.")]
    [SerializeField] private Material sky;
    [Tooltip("Colour of the glow toward the surface at full sunlight.")]
    [SerializeField] private Color surfaceGlow = new Color(0.55f, 0.85f, 0.9f);

    private float[] baseIntensity;
    private Material skyInstance;
    private Camera viewerCamera;
    private MaterialPropertyBlock block;
    private static readonly int BaseColorFactor = Shader.PropertyToID("baseColorFactor");
    private static readonly int HorizonColor = Shader.PropertyToID("_HorizonColor");
    private static readonly int TopColor = Shader.PropertyToID("_TopColor");
    private static readonly int BottomColor = Shader.PropertyToID("_BottomColor");
    private static readonly int SunIntensity = Shader.PropertyToID("_SunIntensity");
    private static readonly int SunDirection = Shader.PropertyToID("_SunDirection");
    private static readonly int WindowStrength = Shader.PropertyToID("_WindowStrength");

    /// <summary>The blended look at the viewer's current depth.</summary>
    public ZoneLook Current { get; private set; }

    private void Start()
    {
        if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
        if (viewer != null) viewerCamera = viewer.GetComponent<Camera>();
        // Behind everything is open water, never the default sky.
        if (sky != null)
        {
            skyInstance = new Material(sky); // so play mode doesn't change the asset
            RenderSettings.skybox = skyInstance;
        }
        if (viewerCamera != null) viewerCamera.clearFlags = skyInstance != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;

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
        if (viewerCamera != null) viewerCamera.backgroundColor = look.waterColor;
        RenderSettings.fogDensity = look.fogDensity;
        RenderSettings.ambientLight = look.ambient;
        RenderSettings.reflectionIntensity = look.reflections;

        for (int i = 0; i < baseIntensity.Length; i++)
            if (sunLights[i] != null) sunLights[i].intensity = baseIntensity[i] * look.sunlight;

        if (backdrop != null)
        {
            // The reef photo only makes sense in clear shallow water; deeper, it would show as a band at the horizon.
            backdrop.enabled = look.backdrop > 0.02f;
            if (block == null) block = new MaterialPropertyBlock();
            backdrop.GetPropertyBlock(block);
            block.SetColor(BaseColorFactor, Color.Lerp(look.waterColor, Color.white, look.backdrop));
            backdrop.SetPropertyBlock(block);
        }

        if (skyInstance != null)
        {
            skyInstance.SetColor(HorizonColor, look.waterColor);
            skyInstance.SetColor(TopColor, Color.Lerp(look.waterColor * 1.6f, surfaceGlow, look.sunlight));
            skyInstance.SetColor(BottomColor, look.waterColor * 0.3f);
            skyInstance.SetFloat(SunIntensity, 0.7f * look.sunlight);
            skyInstance.SetFloat(WindowStrength, look.sunlight * look.sunlight); // the sky window fades out with depth
            if (sunLights != null && sunLights.Length > 0 && sunLights[0] != null)
                skyInstance.SetVector(SunDirection, -sunLights[0].transform.forward);
        }

        if (viewerLight != null)
            viewerLight.intensity = viewerLightDeep * (1f - look.sunlight);

        if (marineSnow != null)
        {
            ParticleSystem.EmissionModule emission = marineSnow.emission;
            emission.rateOverTime = look.marineSnow;
        }
    }

    private void OnDestroy()
    {
        if (skyInstance != null) Destroy(skyInstance);
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
