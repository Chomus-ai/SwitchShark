using UnityEngine;

/// <summary>
/// Keeps the shark out of the terrain, regardless of frame rate.
///
/// SharkRise moves the shark by setting transform.position, which bypasses Unity's
/// physics, so colliders alone never stop it. This script runs after all movement
/// each frame and does three things:
///   1. AVOIDANCE (assist): probes ahead-left, ahead and ahead-right. If terrain is
///      coming up, it gently yaws the shark away from it so the player rarely
///      reaches the wall at all. Weak by design, so the player can still override it.
///   2. SWEEP: sweeps a sphere from last frame's position to this frame's position.
///      If it would hit terrain, the shark stops just short and slides along the
///      surface instead (so it can't tunnel through, however fast it moves).
///   3. GROUND CLEARANCE: casts a ray down from above the shark and keeps it a
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

    [Header("Avoidance steering (assist)")]
    [Tooltip("Master switch. Turn off to get the previous behaviour (sweep and ground clearance only).")]
    [SerializeField] private bool enableAvoidance = true;
    [Tooltip("How far ahead the shark looks for terrain. Roughly: speed x 1 to 1.5 seconds.")]
    [SerializeField] private float lookAhead = 4f;
    [Tooltip("Angle of the left and right probes from the shark's forward direction.")]
    [SerializeField, Range(10f, 80f)] private float probeAngle = 35f;
    [Tooltip("Maximum assist turn rate in degrees per second (at a point-blank obstacle). " +
             "Keep this below the player's own max turn rate so they can always override it.")]
    [SerializeField] private float avoidTurnRate = 45f;
    [Tooltip("How quickly the assist ramps up and down. Higher = snappier, lower = smoother.")]
    [SerializeField] private float avoidSmoothing = 5f;
    [Tooltip("ON: this script rotates the shark directly. " +
             "OFF: it only calculates 'AvoidanceSteer' for your steering script to add in itself.")]
    [SerializeField] private bool rotateShark = true;

    [Header("Optional")]
    [Tooltip("If assigned, the guard is inactive during the Intro so the resting pose is never disturbed.")]
    [SerializeField] private GameManager gameManager;

    /// <summary>
    /// Current assist steering from -1 (turn left) to +1 (turn right).
    /// Read this from your steering script if 'Rotate Shark' is off.
    /// </summary>
    public float AvoidanceSteer { get; private set; }

    private Vector3 lastPosition;
    private float tieBreakSign = 1f;      // which way to turn when a wall is dead ahead
    private bool centerWasBlocked;

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
            AvoidanceSteer = 0f;
            centerWasBlocked = false;
            return;
        }

        // 1. Steer away from terrain that is coming up.
        UpdateAvoidance();

        // 2 and 3. Hard guarantees: never end up inside terrain.
        Vector3 pos = SweepAndSlide(lastPosition, transform.position);
        pos = KeepAboveGround(pos);

        transform.position = pos;
        lastPosition = pos;
    }

    // ---------------------------------------------------------------------
    // Avoidance steering
    // ---------------------------------------------------------------------

    private void UpdateAvoidance()
    {
        float target = enableAvoidance ? ComputeAvoidanceTarget() : 0f;

        // Frame-rate independent smoothing so the assist eases in and out.
        float t = 1f - Mathf.Exp(-avoidSmoothing * Time.deltaTime);
        AvoidanceSteer = Mathf.Lerp(AvoidanceSteer, target, t);

        if (rotateShark && Mathf.Abs(AvoidanceSteer) > 0.001f)
        {
            float yaw = AvoidanceSteer * avoidTurnRate * Time.deltaTime;
            transform.Rotate(0f, yaw, 0f, Space.World);
        }
    }

    /// <summary>
    /// Returns the desired assist steer (-1 left .. +1 right) for this frame, before smoothing.
    /// Each probe gives an 'urgency' from 0 (nothing in range) to 1 (touching). Steering
    /// goes toward whichever side has more room.
    /// </summary>
    private float ComputeAvoidanceTarget()
    {
        // Probe in the horizontal plane around the shark's heading. Vertical
        // clearance is already handled by KeepAboveGround.
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) return 0f; // pointing straight up/down
        forward.Normalize();

        float left = Probe(Quaternion.Euler(0f, -probeAngle, 0f) * forward, out _);
        float right = Probe(Quaternion.Euler(0f, probeAngle, 0f) * forward, out _);
        float center = Probe(forward, out RaycastHit centerHit);

        // Positive = turn right. If the left probe is more blocked, we turn right.
        float steer = left - right;

        if (center > 0f)
        {
            // Wall dead ahead with similar room on both sides: pick a direction from the
            // wall's normal (turn toward the side the wall faces). Latch the choice while
            // the obstacle remains so the shark doesn't dither left/right.
            if (!centerWasBlocked)
            {
                float side = Vector3.Dot(centerHit.normal, transform.right);
                if (Mathf.Abs(side) > 0.1f) tieBreakSign = Mathf.Sign(side);
            }

            if (Mathf.Abs(steer) < 0.1f)
                steer = tieBreakSign * center;
            else
                steer += Mathf.Sign(steer) * center * 0.5f; // reinforce a clear side preference
        }

        centerWasBlocked = center > 0f;
        return Mathf.Clamp(steer, -1f, 1f);
    }

    /// <summary>Casts one probe. Returns 0 if clear, up to 1 as the hit gets closer.</summary>
    private float Probe(Vector3 direction, out RaycastHit hit)
    {
        if (Physics.SphereCast(transform.position, radius, direction, out hit,
                               lookAhead, terrainMask, QueryTriggerInteraction.Ignore))
        {
            return 1f - Mathf.Clamp01(hit.distance / lookAhead);
        }
        return 0f;
    }

    // ---------------------------------------------------------------------
    // Hard collision guarantees (unchanged)
    // ---------------------------------------------------------------------

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

        // Avoidance probes
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) return;
        forward.Normalize();

        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.8f);
        Gizmos.DrawRay(transform.position, forward * lookAhead);
        Gizmos.DrawRay(transform.position, Quaternion.Euler(0f, -probeAngle, 0f) * forward * lookAhead);
        Gizmos.DrawRay(transform.position, Quaternion.Euler(0f, probeAngle, 0f) * forward * lookAhead);
    }
}