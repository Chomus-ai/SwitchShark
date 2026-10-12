using UnityEngine;

/// <summary>
/// Keeps the shark inside a rectangular area (min/max world X and Z).
/// As the shark nears a barrier, it is gradually turned back toward the middle of
/// the area; the closer it gets, the harder the turn. An optional hard clamp
/// guarantees it can never actually cross the barrier.
///
/// Attach to the shark's root object. Runs in LateUpdate, after SharkRise has
/// applied the player's steering and forward movement for the frame.
/// The area is drawn as an orange box in the Scene view (outer = barrier,
/// faint inner box = where turning starts).
/// </summary>
public class SharkBoundary : MonoBehaviour
{
    [Header("Barrier (world X / Z)")]
    [SerializeField] private float minX = -50f;
    [SerializeField] private float maxX = 50f;
    [SerializeField] private float minZ = -50f;
    [SerializeField] private float maxZ = 50f;

    [Header("Turning away")]
    [Tooltip("Turning starts this far inside the barrier and gets stronger toward it.")]
    [SerializeField] private float softMargin = 10f;
    [Tooltip("Degrees per second of turn AT the barrier. Keep this higher than SharkRise's " +
             "Turn Speed, otherwise a player can out-steer the barrier.")]
    [SerializeField] private float turnRate = 120f;
    [Tooltip("Also snap the shark's position back inside if it ever ends up past a barrier.")]
    [SerializeField] private bool hardClamp = true;

    [Header("Optional")]
    [Tooltip("If assigned, the barrier is inactive during the Intro so the shark's resting pose is never disturbed.")]
    [SerializeField] private GameManager gameManager;

    [Header("Scene view gizmo")]
    [SerializeField] private float gizmoHeight = 40f;

    private void OnValidate()
    {
        // Keep the values sensible while editing in the Inspector.
        if (maxX < minX + 1f) maxX = minX + 1f;
        if (maxZ < minZ + 1f) maxZ = minZ + 1f;

        float halfSmallestSide = 0.5f * Mathf.Min(maxX - minX, maxZ - minZ);
        softMargin = Mathf.Clamp(softMargin, 0f, halfSmallestSide);
        turnRate = Mathf.Max(0f, turnRate);
    }

    private void Start()
    {
        Vector3 p = transform.position;
        if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ)
            Debug.LogWarning("SharkBoundary: the shark starts outside the barrier. " +
                             "Adjust the min/max values so the start position is inside.", this);
    }

    private void LateUpdate()
    {
        if (gameManager != null && gameManager.CurrentState == GameState.Intro) return;

        Vector3 pos = transform.position;

        float strength = WallStrength(pos);
        if (strength > 0f)
            TurnTowardCentre(pos, strength);

        if (hardClamp)
        {
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
            pos.z = Mathf.Clamp(pos.z, minZ, maxZ);
            transform.position = pos;
        }
    }

    /// <summary>0 = comfortably inside, 1 = at (or past) the nearest barrier.</summary>
    private float WallStrength(Vector3 pos)
    {
        // Distance to the closest barrier; negative if already outside.
        float edgeX = Mathf.Min(pos.x - minX, maxX - pos.x);
        float edgeZ = Mathf.Min(pos.z - minZ, maxZ - pos.z);
        float edge = Mathf.Min(edgeX, edgeZ);

        if (softMargin <= 0.001f)
            return edge <= 0f ? 1f : 0f;

        float t = 1f - Mathf.Clamp01(edge / softMargin);
        return Mathf.SmoothStep(0f, 1f, t); // gentle at first, firm near the barrier
    }

    private void TurnTowardCentre(Vector3 pos, float strength)
    {
        Vector3 centre = new Vector3((minX + maxX) * 0.5f, pos.y, (minZ + maxZ) * 0.5f);

        Vector3 toCentre = centre - pos;
        toCentre.y = 0f;

        Vector3 forward = transform.forward;
        forward.y = 0f;

        if (toCentre.sqrMagnitude < 0.0001f || forward.sqrMagnitude < 0.0001f) return;

        // Signed angle (degrees) from where the shark faces to the direction of the centre.
        float angle = Vector3.SignedAngle(forward, toCentre, Vector3.up);

        float maxStep = turnRate * strength * Time.deltaTime;
        float step = Mathf.Clamp(angle, -maxStep, maxStep);

        transform.Rotate(0f, step, 0f, Space.World);
    }

    private void OnDrawGizmos()
    {
        Vector3 centre = new Vector3((minX + maxX) * 0.5f, transform.position.y, (minZ + maxZ) * 0.5f);

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
        Gizmos.DrawWireCube(centre, new Vector3(maxX - minX, gizmoHeight, maxZ - minZ));

        float innerWidth = (maxX - minX) - 2f * softMargin;
        float innerDepth = (maxZ - minZ) - 2f * softMargin;
        if (innerWidth > 0f && innerDepth > 0f)
        {
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.35f);
            Gizmos.DrawWireCube(centre, new Vector3(innerWidth, gizmoHeight, innerDepth));
        }
    }
}
