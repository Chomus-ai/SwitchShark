using UnityEngine;

/// <summary>
/// Makes the shark's swimming feel physical, on top of its animation:
///   Sediment   : silt clouds kicked up from the seabed when swimming close to it (bigger when faster)
///   Tail wake  : specks stirred up behind the tail. Deeper down they glow blue-green, like real
///                bioluminescent plankton that lights up when disturbed
///   Water push : the drifting marine snow is pushed aside and swirls around the body
///   Speed rush : a burst of stirred water and a small camera FOV kick when swimming fast or boosting
/// All effect objects are created at runtime under "Shark Swim Effects" and follow the shark.
/// Put this on the shark root (the object with SharkRise). Set up by SwitchShark > Build Underwater Scene.
/// </summary>
public class SharkSwimEffects : MonoBehaviour
{
    [SerializeField] private SharkRise shark;
    [Tooltip("Tail bone the wake is emitted from (follows the swim animation). Leave empty to use the back of the body.")]
    [SerializeField] private Transform tail;
    [SerializeField] private LayerMask seabedMask = 1 << 3;

    [Header("Materials")]
    [Tooltip("Soft alpha-blended particle material, for silt.")]
    [SerializeField] private Material dustMaterial;
    [Tooltip("Soft additive particle material, for the glowing wake.")]
    [SerializeField] private Material glowMaterial;
    [Tooltip("The marine snow around the camera, pushed aside by the shark. Found automatically if empty.")]
    [SerializeField] private ParticleSystem marineSnow;

    [Header("Speed")]
    [Tooltip("Normal swimming speed (m/s). Effects scale with speed relative to this.")]
    [SerializeField] private float cruiseSpeed = 3f;

    [Header("Sediment")]
    [Tooltip("Silt is kicked up when the belly is closer than this to the seabed.")]
    [SerializeField] private float sedimentHeight = 2.2f;
    [SerializeField] private float sedimentRate = 26f;
    [SerializeField] private Color shallowSilt = new Color(0.78f, 0.74f, 0.62f, 0.32f);
    [SerializeField] private Color deepSilt = new Color(0.32f, 0.34f, 0.36f, 0.3f);

    [Header("Tail wake")]
    [SerializeField] private float wakeRate = 60f;
    [SerializeField] private Color shallowWake = new Color(0.75f, 0.85f, 0.85f, 0.35f);
    [ColorUsage(true, true)]
    [SerializeField] private Color deepWake = new Color(0.3f, 2.2f, 2f, 1f);

    [Header("Water push")]
    [SerializeField] private float pushRadius = 1.4f;
    [SerializeField] private float pushStrength = 0.8f;

    [Header("Speed rush")]
    [SerializeField] private float fovKick = 6f;

    private ParticleSystem silt, wake;
    private ParticleSystemForceField push;
    private Transform root;
    private Vector3 lastPosition, velocity;
    private float siltBudget, wakeBudget, rush, baseFov;
    private bool wasFast;
    private Camera cam;
    private Behaviour brain;

    private void Start()
    {
        if (shark == null) shark = GetComponent<SharkRise>();
        if (tail == null) tail = FindDeep(transform, "spine_11");
        cam = Camera.main;
        if (cam != null)
        {
            baseFov = cam.fieldOfView;
            brain = cam.GetComponent("CinemachineBrain") as Behaviour;
            if (marineSnow == null)
            {
                Transform snow = cam.transform.Find("Marine Snow");
                if (snow != null) marineSnow = snow.GetComponent<ParticleSystem>();
            }
        }

        // Effects live outside the shark so its tiny root scale doesn't shrink them.
        root = new GameObject("Shark Swim Effects").transform;
        silt = MakeSystem("Sediment", dustMaterial, 2000, lifetime: new Vector2(4f, 7f), growTo: 3.2f, noise: 0.15f);
        wake = MakeSystem("Tail Wake", glowMaterial != null ? glowMaterial : dustMaterial, 1500, lifetime: new Vector2(1.2f, 2.4f), growTo: 0.6f, noise: 0.35f);

        var pushGo = new GameObject("Water Push");
        pushGo.transform.SetParent(root, false);
        push = pushGo.AddComponent<ParticleSystemForceField>();
        push.shape = ParticleSystemForceFieldShape.Sphere;
        push.endRange = pushRadius;

        foreach (ParticleSystem ps in new[] { marineSnow, wake })
        {
            if (ps == null) continue;
            var forces = ps.externalForces;
            forces.enabled = true;
            forces.multiplier = 1f;
        }

        lastPosition = transform.position;
    }

    private void OnDestroy()
    {
        if (root != null) Destroy(root.gameObject);
        if (cam != null && brain != null && !brain.enabled) cam.fieldOfView = baseFov;
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 position = transform.position;
        velocity = Vector3.Lerp(velocity, (position - lastPosition) / dt, 1f - Mathf.Exp(-dt * 8f));
        lastPosition = position;

        float speed = shark != null ? Mathf.Max(shark.CurrentSpeed, velocity.magnitude) : velocity.magnitude;
        float effort = Mathf.Clamp01(speed / Mathf.Max(0.1f, cruiseSpeed));          // 0 resting .. 1 cruising
        float fast = Mathf.Clamp01((speed / Mathf.Max(0.1f, cruiseSpeed) - 1.2f) / 0.6f); // 0 .. 1 at ~1.8x cruise
        float deep = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.75f, OceanZones.DepthFraction(position.y)));

        root.SetPositionAndRotation(position, transform.rotation);

        UpdateSediment(position, effort, deep, dt);
        UpdateWake(effort, fast, deep, dt);
        UpdatePush(effort);
        UpdateRush(fast, dt);
    }

    private void UpdateSediment(Vector3 position, float effort, float deep, float dt)
    {
        if (effort < 0.05f) return;
        if (!Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out RaycastHit ground, sedimentHeight + 0.5f,
                             seabedMask, QueryTriggerInteraction.Ignore)) return;

        float closeness = 1f - Mathf.Clamp01((ground.distance - 0.5f) / sedimentHeight);
        siltBudget += sedimentRate * effort * closeness * closeness * dt;
        Color color = Color.Lerp(shallowSilt, deepSilt, deep);

        while (siltBudget >= 1f)
        {
            siltBudget -= 1f;
            // Along the underside of the body, spreading out and up from the seabed.
            Vector3 along = transform.forward * Random.Range(-0.8f, 0.4f);
            Vector3 side = Vector3.Cross(Vector3.up, transform.forward).normalized * Random.Range(-0.4f, 0.4f);
            Vector3 spread = (side * 2f + Vector3.up * Random.Range(0.4f, 1f)) * Random.Range(0.15f, 0.45f);
            silt.Emit(new ParticleSystem.EmitParams
            {
                position = ground.point + along + side + Vector3.up * 0.05f,
                velocity = spread + velocity * 0.15f,
                startSize = Random.Range(0.25f, 0.6f),
                startColor = color,
                applyShapeToPosition = false,
            }, 1);
        }
    }

    private void UpdateWake(float effort, float fast, float deep, float dt)
    {
        wakeBudget += wakeRate * (effort * effort + fast) * dt;
        Vector3 origin = tail != null ? tail.position : transform.position - transform.forward * 0.7f;
        Color color = Color.Lerp(shallowWake, deepWake, deep);

        while (wakeBudget >= 1f)
        {
            wakeBudget -= 1f;
            wake.Emit(new ParticleSystem.EmitParams
            {
                position = origin + Random.insideUnitSphere * 0.25f,
                velocity = -transform.forward * Random.Range(0.05f, 0.3f) + Random.insideUnitSphere * 0.3f,
                startSize = Random.Range(0.01f, 0.025f) * (1f + deep),
                startColor = color,
                applyShapeToPosition = false,
            }, 1);
        }
    }

    private void UpdatePush(float effort)
    {
        // Pushes particles backward along the body and outward from it, with a little swirl.
        push.directionZ = -pushStrength * effort;
        push.gravity = -pushStrength * 0.6f * effort;
        push.rotationSpeed = 0.6f * effort;
    }

    private void UpdateRush(float fast, float dt)
    {
        bool isFast = fast > 0.5f;
        if (isFast && !wasFast)
        {
            // A burst of stirred water as the shark kicks off.
            for (int i = 0; i < 40; i++)
                wake.Emit(new ParticleSystem.EmitParams
                {
                    position = transform.position + Random.insideUnitSphere * 0.4f - transform.forward * 0.3f,
                    velocity = Random.insideUnitSphere * 0.8f - transform.forward * 0.6f,
                    startSize = Random.Range(0.02f, 0.06f),
                    applyShapeToPosition = false,
                }, 1);
        }
        wasFast = isFast;

        rush = Mathf.MoveTowards(rush, fast, dt * 2f);
        // Only when the camera isn't driven by Cinemachine (which sets its own lens).
        if (cam != null && (brain == null || !brain.enabled))
            cam.fieldOfView = baseFov + fovKick * Mathf.SmoothStep(0f, 1f, rush);
    }

    private ParticleSystem MakeSystem(string name, Material material, int max, Vector2 lifetime, float growTo, float noise)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime.x, lifetime.y);
        main.startSpeed = 0f;
        main.maxParticles = max;
        main.playOnAwake = false;
        main.gravityModifier = 0.002f; // settles very slowly

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        var shape = ps.shape;
        shape.enabled = false;

        var drag = ps.limitVelocityOverLifetime;
        drag.enabled = true;
        drag.limit = 0f;
        drag.dampen = 0.05f; // water slows everything down
        drag.drag = 0.6f;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, growTo));

        var fade = ps.colorOverLifetime;
        fade.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                         new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0.6f, 0.6f), new GradientAlphaKey(0f, 1f) });
        fade.color = gradient;

        var n = ps.noise;
        n.enabled = true;
        n.strength = noise;
        n.frequency = 0.4f;
        n.scrollSpeed = 0.2f;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.sortingFudge = 1f;
        renderer.maxParticleSize = 0.03f; // specks right in front of the camera stay small on screen

        ps.Play();
        return ps;
    }

    private static Transform FindDeep(Transform parent, string name)
    {
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }
}
