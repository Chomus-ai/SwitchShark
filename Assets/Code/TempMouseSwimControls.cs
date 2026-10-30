using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// TEMPORARY keyboard + mouse controls for testing without a Joy-Con or Arduino,
/// ported from the Lantern Shark demo (SharkSwimPath + OrbitFollowCamera).
///   Space / W / Enter / click : start from the Intro
///   Mouse        : look around. The mouse is captured when the game starts (or on click); Esc releases it.
///   W / S        : swim toward / away from where the camera looks (look up and hold W to rise)
///   A / D        : swim left / right
///   Shift        : swim faster
///   Scroll       : zoom in / out
///   F2           : switch between these controls and the normal SharkRise + Cinemachine setup
///
/// While active, SharkRise and the camera's CinemachineBrain are disabled and this script
/// moves the shark and the camera itself. SharkBoundary and STerrainGuard still apply.
/// Delete the "Temp Mouse Controls" object to go back to the real controls.
/// </summary>
[DefaultExecutionOrder(200)] // camera follows after SharkBoundary and STerrainGuard have moved the shark
public class TempMouseSwimControls : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [SerializeField] private SharkRise shark;
    [Tooltip("Leave empty to use Camera.main.")]
    [SerializeField] private Camera cam;
    [SerializeField] private bool startActive = true;

    [Header("Swimming")]
    [Tooltip("Metres per second. The shark is about 1.5 m long.")]
    [SerializeField] private float speed = 3f;
    [SerializeField] private float fastMultiplier = 2f;
    [Tooltip("Degrees per second the body turns to face the camera direction.")]
    [SerializeField] private float turnRate = 300f;
    [SerializeField] private float bankDegrees = 14f;

    [Header("Camera")]
    [SerializeField] private float distance = 2f;
    [SerializeField] private Vector2 distanceRange = new Vector2(0.8f, 8f);
    [SerializeField] private float mouseSensitivity = 0.12f;
    [SerializeField] private float followSmoothing = 0.12f;
    [SerializeField] private float cameraRadius = 0.1f;
    [Tooltip("How far above the body centre the camera aims.")]
    [SerializeField] private float lookHeight = 0.3f;
    [Tooltip("The camera stays at least this far above the seabed.")]
    [SerializeField] private float floorClearance = 0.6f;
    [Tooltip("What the camera can't pass through: the seabed and rocks (Terrain layer). Fish are ignored.")]
    [SerializeField] private LayerMask cameraCollision = 1 << 3;
    [SerializeField] private Vector2 pitchLimits = new Vector2(-50f, 70f);

    private bool active;
    private Vector3 bodyCentre;   // middle of the shark's body, in its local space
    private float currentDistance;
    private Behaviour brain;
    private float yaw, pitch = 10f;
    private Vector3 velocity, velocityDamping, focus, focusVelocity;
    private float currentPitch, pitchDamping, currentBank, bankVelocity;
    private Vector3 startPosition;
    private Quaternion startRotation;

    private Vector3 Aim => Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;

    private IEnumerator Start()
    {
        if (cam == null) cam = Camera.main;
        if (gameManager == null || shark == null || cam == null)
        {
            Debug.LogError("TempMouseSwimControls: assign Game Manager, Shark and Camera.", this);
            enabled = false;
            yield break;
        }

        brain = cam.GetComponent("CinemachineBrain") as Behaviour;
        bodyCentre = BodyCentre(shark.transform);
        currentDistance = distance;
        startPosition = shark.transform.position;
        startRotation = shark.transform.rotation;
        gameManager.StateChanged += HandleStateChanged;

        // Let SharkRise run its own Start first, so switching back with F2 still works.
        yield return null;
        SetActive(startActive);
    }

    private void OnDestroy()
    {
        if (gameManager != null) gameManager.StateChanged -= HandleStateChanged;
        SetCursorCaptured(false);
    }

    private void SetActive(bool on)
    {
        active = on;
        shark.enabled = !on;
        if (brain != null) brain.enabled = !on;
        if (!on) SetCursorCaptured(false);

        yaw = shark.transform.eulerAngles.y;
        focus = shark.transform.TransformPoint(bodyCentre);
        velocity = velocityDamping = Vector3.zero;
        Debug.Log(on ? "[TempMouseSwimControls] Mouse + keyboard controls ON (F2 to switch back)."
                     : "[TempMouseSwimControls] Normal controls restored (F2 for mouse + keyboard).");
    }

    /// <summary>True while these temporary mouse + keyboard controls are in charge (read by ControlsHelp).</summary>
    public bool IsActive => active;

    private void HandleStateChanged(GameState previous, GameState next)
    {
        if (!active) return;
        velocity = velocityDamping = Vector3.zero;
        if (next == GameState.Rise && Application.isFocused) SetCursorCaptured(true);
        if (next == GameState.Intro)
        {
            shark.transform.SetPositionAndRotation(startPosition, startRotation);
            shark.CurrentSpeed = 0f;
            yaw = startRotation.eulerAngles.y;
            focus = shark.transform.TransformPoint(bodyCentre);
        }
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (shark == null) return;
        if (keyboard != null && keyboard.f2Key.wasPressedThisFrame) SetActive(!active);
        if (!active) return;

        // On the start screen, any swim key or a click also starts the game (Space is handled by GameManager).
        if (gameManager.CurrentState == GameState.Intro &&
            ((keyboard != null && (keyboard.wKey.wasPressedThisFrame || keyboard.enterKey.wasPressedThisFrame)) ||
             (mouse != null && mouse.leftButton.wasPressedThisFrame && Application.isFocused)))
            gameManager.RequestStart();

        // --- Mouse look ---
        if (mouse != null)
        {
            bool captured = Cursor.lockState == CursorLockMode.Locked;
            if (!captured && mouse.leftButton.wasPressedThisFrame && Application.isFocused) SetCursorCaptured(true);
            else if (captured && keyboard != null && keyboard.escapeKey.wasPressedThisFrame) SetCursorCaptured(false);
            else if (captured)
            {
                Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
                yaw += delta.x;
                pitch = Mathf.Clamp(pitch - delta.y, pitchLimits.x, pitchLimits.y);
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f) distance *= 1f - Mathf.Sign(scroll) * 0.1f;
            distance = Mathf.Clamp(distance, distanceRange.x, distanceRange.y);
        }

        // --- Swimming ---
        float dt = Time.deltaTime;
        Vector3 requested = Vector3.zero;
        Transform t = shark.transform;
        float forward = 0f, strafe = 0f;

        switch (gameManager.CurrentState)
        {
            // At the surface the player keeps control (the game returns to the Intro after a few seconds).
            case GameState.Rise:
            case GameState.Surface:
                if (keyboard != null)
                {
                    forward = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                    strafe = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                    Vector2 input = Vector2.ClampMagnitude(new Vector2(strafe, forward), 1f);
                    Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
                    float s = speed * (keyboard.shiftKey.isPressed ? fastMultiplier : 1f) * input.magnitude;
                    if (input.sqrMagnitude > 0f) requested = (Aim * input.y + right * input.x).normalized * s;
                }
                break;
        }

        velocity = Vector3.SmoothDamp(velocity, requested, ref velocityDamping, 0.22f, Mathf.Infinity, dt);
        Vector3 p = t.position + velocity * dt;
        p.y = Mathf.Clamp(p.y, startPosition.y, OceanZones.SurfaceY - 0.2f); // between the seabed and just under the waves
        t.position = p;
        shark.CurrentSpeed = velocity.magnitude;

        // --- Body orientation (over-the-shoulder): the shark faces where the camera looks.
        //     A / D slide it sideways without turning; the nose follows the view up/down while swimming forward. ---
        float heading = t.eulerAngles.y;
        float yawStep = 0f;
        float desiredPitch = 0f;
        if (gameManager.CurrentState != GameState.Intro)
        {
            yawStep = Mathf.Clamp(Mathf.DeltaAngle(heading, yaw), -turnRate * dt, turnRate * dt);
            if (forward > 0f && velocity.sqrMagnitude > 0.01f) desiredPitch = Mathf.Clamp(pitch, -60f, 60f);
        }
        heading += yawStep;

        float turnSpeed = dt > 0f ? yawStep / dt : 0f;
        float lean = -Mathf.Clamp(turnSpeed / 90f, -1f, 1f) - strafe * 0.6f; // bank into turns and slides
        currentBank = Mathf.SmoothDamp(currentBank, Mathf.Clamp(lean, -1f, 1f) * bankDegrees,
            ref bankVelocity, 0.28f, Mathf.Infinity, dt);
        currentPitch = Mathf.SmoothDampAngle(currentPitch, desiredPitch, ref pitchDamping, 0.25f, Mathf.Infinity, dt);
        t.rotation = Quaternion.Euler(0f, heading, 0f) * Quaternion.Euler(currentPitch, 0f, currentBank);
    }

    private void LateUpdate()
    {
        if (!active || shark == null || cam == null) return;

        // Orbit the middle of the body (the shark's pivot is at its tail), slightly above it.
        Vector3 target = shark.transform.TransformPoint(bodyCentre) + Vector3.up * lookHeight;
        focus = Vector3.SmoothDamp(focus, target, ref focusVelocity, followSmoothing);

        // Pull in when the seabed or a rock is between the shark and the camera (not for fish),
        // then ease back out so the view doesn't jump.
        Vector3 backward = -Aim;
        float wanted = distance;
        if (Physics.SphereCast(focus, cameraRadius, backward, out RaycastHit hit, distance, cameraCollision, QueryTriggerInteraction.Ignore))
            wanted = Mathf.Max(0.3f, hit.distance - 0.1f);
        currentDistance = wanted < currentDistance ? wanted
                        : Mathf.MoveTowards(currentDistance, wanted, 4f * Time.deltaTime);
        Vector3 position = focus + backward * currentDistance;

        // Never below the seabed or above the surface.
        if (Physics.Raycast(position + Vector3.up * 30f, Vector3.down, out RaycastHit ground, 60f, cameraCollision, QueryTriggerInteraction.Ignore))
            position.y = Mathf.Max(position.y, ground.point.y + floorClearance);
        position.y = Mathf.Min(position.y, OceanZones.SurfaceY - 0.2f);

        cam.transform.position = position;
        cam.transform.LookAt(focus);
    }

    private static Vector3 BodyCentre(Transform root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        Bounds? bounds = null;
        foreach (Renderer r in renderers)
        {
            if (r is ParticleSystemRenderer) continue;
            if (bounds == null) bounds = r.bounds;
            else { Bounds b = bounds.Value; b.Encapsulate(r.bounds); bounds = b; }
        }
        return bounds.HasValue ? root.InverseTransformPoint(bounds.Value.center) : Vector3.zero;
    }

    private void SetCursorCaptured(bool captured)
    {
        Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !captured;
    }
}
