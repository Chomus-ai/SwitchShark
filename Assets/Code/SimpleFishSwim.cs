using UnityEngine;

/// <summary>
/// Makes a fish swim slowly forward, swaying gently left/right and drifting upward.
/// Add to each fish prefab. "Forward" is the prefab's blue Z axis.
/// Pool-safe: re-initialises every time the fish is re-enabled.
/// </summary>
public class SimpleFishSwim : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float speed = 1.5f;

    [Header("Heading (left / right)")]
    [Tooltip("Max sideways swing from the starting heading, in degrees. 0 = perfectly straight.")]
    [SerializeField] private float weaveAmplitude = 8f;
    [SerializeField] private float weaveFrequency = 0.3f;

    [Header("Climb (up / down)")]
    [Tooltip("Each fish picks a random climb angle in this range (degrees, positive = nose up). Keep min >= 0 so fish never dive. Vertical speed = speed x sin(angle).")]
    [SerializeField] private Vector2 climbAngleRange = new Vector2(0f, 12f);
    [Tooltip("Small nose up/down wobble so the climb looks natural, in degrees.")]
    [SerializeField] private float pitchWobble = 2f;
    [SerializeField] private float wobbleFrequency = 0.4f;

    [Header("Limits")]
    [Tooltip("World height of the water surface. Fish level out as they approach it. Match GameManager maxHeight.")]
    [SerializeField] private float ceilingY = 100f;
    [Tooltip("Distance below the ceiling over which the climb fades to level.")]
    [SerializeField] private float ceilingFade = 5f;
    [SerializeField] private bool avoidTerrain = true;
    [SerializeField] private float terrainClearance = 0.5f;

    private bool initialised;
    private float baseYaw;
    private float climbAngle;
    private float phase;
    private Terrain terrain;

    private void OnEnable()
    {
        // Don't read the rotation here: the spawner sets it right after the fish is enabled.
        initialised = false;
    }

    private void Init()
    {
        baseYaw = transform.eulerAngles.y;
        climbAngle = Random.Range(climbAngleRange.x, climbAngleRange.y);
        phase = Random.value * 10f;          // so fish don't all sway in sync
        terrain = Terrain.activeTerrain;
        initialised = true;
    }

    private void Update()
    {
        if (!initialised) Init();

        float t = Time.time + phase;

        // Left/right sway around the starting heading.
        float yaw = baseYaw + Mathf.Sin(t * weaveFrequency * Mathf.PI * 2f) * weaveAmplitude;

        // Climb, fading to level near the surface, plus a little wobble.
        float ceilingFactor = Mathf.Clamp01((ceilingY - transform.position.y) / Mathf.Max(0.01f, ceilingFade));
        float pitchUp = climbAngle * ceilingFactor
                        + Mathf.Sin(t * wobbleFrequency * Mathf.PI * 2f) * pitchWobble;

        // In Unity a NEGATIVE X rotation tilts the nose up.
        transform.rotation = Quaternion.Euler(-pitchUp, yaw, 0f);
        transform.position += transform.forward * speed * Time.deltaTime;

        if (avoidTerrain && terrain != null)
        {
            Vector3 p = transform.position;
            float floorY = terrain.SampleHeight(p) + terrain.transform.position.y + terrainClearance;
            if (p.y < floorY)
            {
                p.y = floorY;
                transform.position = p;
            }
        }
    }
}