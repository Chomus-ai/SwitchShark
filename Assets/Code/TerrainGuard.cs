using UnityEngine;

/// <summary>
/// Keeps the shark out of the terrain, regardless of frame rate.
///
/// SharkRise moves the shark by setting transform.position, which bypasses Unity's
/// physics, so colliders alone never stop it. This script runs after all movement
/// each frame and does two things:
///   1. SWEEP: sweeps a sphere from last frame's position to this frame's position.
///      If it would hit terrain, the shark stops just short and slides along the
///      surface instead (so it can't tunnel through, however fast it moves).
///   2. GROUND CLEARANCE: casts a ray down from above the shark and keeps it a
///      small distance above the surface. This also pulls the shark back out if it
///      ever ends up inside the terrain (for example after a teleport).
///
/// Attach to the shark's root. Set 'Terrain Mask' to the layer(s) your terrain and
/// rocks are on. Trigger colliders (fish, the Mouth) are ignored.
/// </summary>
[DefaultExecutionOrder(100)] // run after SharkBoundary and other LateUpdate scripts
public class SharkTerrainGuard : MonoBehaviour
{
    [Header("What counts as terrain")]
    [Tooltip("Put terrain and rocks on their own layer and select it here. " +
             "Do NOT include the shark's own layer.")]
    [SerializeField] private LayerMask terrainMask;

    [Header("Shark size")]
    [Tooltip("Radius of the sphere used to represent the shark's body in the sweep.")]
    [SerializeField] private float radius = 0.6f;
    [Tooltip("Gap kept between the shark and a surface it stops against.")]
    [SerializeField] private float skin = 0.05f;

    [Header("Ground clearance")]
    [Tooltip("The shark is kept at least this far above the ground below it.")]
    [SerializeField] private float groundClearance = 0.5f;
    [Tooltip("The ground ray starts this far above the shark (lets it recover from being inside terrain).")]
    [SerializeField] private float groundProbeHeight = 20f;
    [Tooltip("How far below the shark the ground ray reaches.")]
    [SerializeField] private float groundProbeDepth = 20f;

    [Header("Optional")]
    [Tooltip("If assigned, the guard is inactive during the Intro so the resting pose is never disturbed.")]
    [SerializeField] private GameManager gameManager;

    private Vector3 lastPosition;

    private void Start()
    {
        lastPosition = transform.position;

        if (terrainMask.value == 0)
            Debug.LogWarning("SharkTerrainGuard: 'Terrain Mask' is empty, so nothing will be detected. " +
                             "Put your terrain on a layer and select it.", this);
    }

    private void LateUpdate()
    {
        // During the Intro the shark may be teleported back to its start pose,
        // so just keep tracking its position and don't sweep across the jump.
        if (gameManager != null && gameManager.CurrentState == GameState.Intro)
        {
            lastPosition = transform.position;
            return;
        }

        Vector3 pos = SweepAndSlide(lastPosition, transform.position);
        pos = KeepAboveGround(pos);

        transform.position = pos;
        lastPosition = pos;
    }

    /// <summary>
    /// Moves from 'from' toward 'to', stopping at any terrain hit and sliding along it.
    /// A few iterations handle corners where sliding runs into a second surface.
    /// </summary>
    private Vector3 SweepAndSlide(Vector3 from, Vector3 to)
    {
        Vector3 pos = from;
        Vector3 remaining = to - from;

        for (int i = 0; i < 3; i++)
        {
            float distance = remaining.magnitude;
            if (distance < 0.0001f) break;

            Vector3 direction = remaining / distance;

            if (!Physics.SphereCast(pos, radius, direction, out RaycastHit hit,
                                    distance + skin, terrainMask, QueryTriggerInteraction.Ignore))
            {
                return pos + remaining; // clear path
            }

            // Move up to the surface (minus the skin gap)...
            float travel = Mathf.Max(0f, hit.distance - skin);
            pos += direction * travel;

            // ...then slide the rest of the movement along the surface.
            Vector3 leftover = remaining - direction * travel;
            remaining = Vector3.ProjectOnPlane(leftover, hit.normal);
        }

        return pos;
    }

    private Vector3 KeepAboveGround(Vector3 pos)
    {
        Vector3 origin = pos + Vector3.up * groundProbeHeight;
        float rayLength = groundProbeHeight + groundProbeDepth;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayLength,
                            terrainMask, QueryTriggerInteraction.Ignore))
        {
            float minY = hit.point.y + groundClearance;
            if (pos.y < minY) pos.y = minY;
        }

        return pos;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
