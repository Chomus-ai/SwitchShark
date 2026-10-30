using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Shark controller for the rising-only game. Behaviour depends on GameManager's state:
///   Intro   : press the Joy-Con action button to start (Space also works via GameManager)
///   Rise    : the shark swims forward automatically along its nose direction.
///             Tilt the Joy-Con left/right (or A / D) to turn.
///             Tilt it up/down (or W / S) to pitch the nose up/down. Because the shark swims
///             along its nose, pitching up makes it climb a little.
///             Pressing the button (or Space) gives a short burst of speed.
///   Surface : the shark levels out and glides forward slowly, no input.
/// Joy-Con input is used when a Joy-Con is connected; the keyboard always works too.
/// </summary>
public class SharkRise : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [Tooltip("Optional. Leave empty to use the keyboard only.")]
    [SerializeField] private ControllerInputProvider joycon;
    [Tooltip("Optional. A child object holding the shark MODEL (with identity rotation). " +
             "It is pitched and banked to match the movement. Leave empty to skip.")]
    [SerializeField] private Transform visual;

    [Header("Forward movement")]
    [SerializeField] private float forwardSpeed = 5f;

    [Header("Steering (tilt left/right, or A / D)")]
    [Tooltip("Degrees per second at full tilt.")]
    [SerializeField] private float turnSpeed = 60f;
    [Tooltip("Seconds of smoothing on steering. Hides jitter from pressing the button while tilting.")]
    [SerializeField] private float turnSmoothing = 0.15f;

    [Header("Pitch (tilt up/down, or W / S)")]
    [Tooltip("Nose-up angle at full upward tilt. The shark swims along its nose, so climb speed is " +
             "roughly speed x sin(angle).")]
    [SerializeField] private float maxClimbAngle = 20f;
    [Tooltip("Nose-down angle at full downward tilt.")]
    [SerializeField] private float maxDiveAngle = 15f;
    [Tooltip("Seconds of smoothing on the pitch angle.")]
    [SerializeField] private float pitchSmoothing = 0.25f;

    [Header("Boost (button press / Space)")]
    [Tooltip("Extra speed at the start of a burst. It fades out over Boost Duration.")]
    [SerializeField] private float boostSpeedBonus = 4f;
    [SerializeField] private float boostDuration = 0.6f;
    [Tooltip("Debounce: minimum seconds between bursts. Presses sooner than this are ignored, " +
             "so spamming the button can't keep the shark boosted.")]
    [SerializeField] private float boostCooldown = 0.8f;

    [Header("Visual flair (only used if 'Visual' is assigned)")]
    [Tooltip("Degrees of roll at full turn. Flip the sign if it leans the wrong way.")]
    [SerializeField] private float maxBank = 25f;
    [SerializeField] private float visualSmoothing = 6f;

    [Header("Pose")]
    [Tooltip("Snap back to the starting position and rotation when returning to the Intro.")]
    [SerializeField] private bool resetPoseOnIntro = true;

    /// <summary>Current swimming speed (forward speed plus any boost). Handy for animation and effects.</summary>
    public float CurrentSpeed { get; internal set; } // internal: TempMouseSwimControls drives it while active

    /// <summary>1 right after a boost press, fading to 0. Handy for camera FOV kicks or particles.</summary>
    public float BoostFraction => boostDuration > 0f ? Mathf.Clamp01(boostRemaining / boostDuration) : 0f;

    // Captured at Start: the shark is assumed to begin resting on the seabed.
    private float seabedY;
    private Vector3 startPosition;
    private Quaternion startRotation;

    private float smoothedTurn;
    private float pitchAngle;       // degrees, positive = nose up
    private float bank;
    private float boostRemaining;
    private float lastBoostTime = float.NegativeInfinity;

    // Ignore input on the frame a state changes, so the press that starts
    // the game (Intro -> Rise) doesn't also trigger the first boost.
    private int stateChangeFrame = -1;

    private bool JoyconReady => joycon != null && joycon.Connected;

    private void Awake()
    {
        if (gameManager == null)
            Debug.LogError("SharkRise: 'gameManager' is not assigned.", this);
    }

    private void Start()
    {
        startPosition = transform.position;
        startRotation = transform.rotation;
        seabedY = startPosition.y;
        CurrentSpeed = 0f;
    }

    private void OnEnable()
    {
        if (gameManager != null) gameManager.StateChanged += HandleStateChanged;
    }

    private void OnDisable()
    {
        if (gameManager != null) gameManager.StateChanged -= HandleStateChanged;
    }

    private void HandleStateChanged(GameState previous, GameState next)
    {
        stateChangeFrame = Time.frameCount;

        if (next == GameState.Rise)
        {
            smoothedTurn = 0f;
            pitchAngle = 0f;
            boostRemaining = 0f;
            lastBoostTime = float.NegativeInfinity;

            // Whatever way the player is holding the Joy-Con now counts as "level and straight ahead".
            if (joycon != null) joycon.CalibrateTilt();
        }

        if (next == GameState.Intro && resetPoseOnIntro)
        {
            transform.SetPositionAndRotation(startPosition, startRotation);
            smoothedTurn = 0f;
            pitchAngle = 0f;
            boostRemaining = 0f;
        }
    }

    private void Update()
    {
        if (gameManager == null) return;
        if (Time.frameCount == stateChangeFrame) return;

        Keyboard keyboard = Keyboard.current; // null if no keyboard is connected

        switch (gameManager.CurrentState)
        {
            case GameState.Intro:
                // GameManager already handles the keyboard start key.
                if (JoyconReady && joycon.ActionPressedThisFrame)
                    gameManager.RequestStart();
                CurrentSpeed = 0f;
                UpdateVisual(0f);
                break;

            case GameState.Rise:
                UpdateRise(keyboard);
                break;

            case GameState.Surface:
                UpdateSurface();
                break;
        }
    }

    private void UpdateRise(Keyboard keyboard)
    {
        float dt = Time.deltaTime;

        // --- Boost: a press starts a short burst of extra speed (with a cooldown). ---
        bool tap = (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                   || (JoyconReady && joycon.ActionPressedThisFrame);

        if (tap && Time.time - lastBoostTime >= boostCooldown)
        {
            boostRemaining = boostDuration;
            lastBoostTime = Time.time;
        }

        // Quadratic fade: strong at the start of the burst, easing out smoothly.
        float boost = BoostFraction;
        CurrentSpeed = forwardSpeed + boostSpeedBonus * boost * boost;
        boostRemaining = Mathf.Max(0f, boostRemaining - dt);

        // --- Steering (left/right): tilt or A / D, smoothed. ---
        float turnInput = 0f;
        if (keyboard != null)
        {
            if (keyboard.dKey.isPressed) turnInput += 1f;
            if (keyboard.aKey.isPressed) turnInput -= 1f;
        }
        if (JoyconReady)
            turnInput += joycon.Steer;
        turnInput = Mathf.Clamp(turnInput, -1f, 1f);

        float kTurn = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, turnSmoothing));
        smoothedTurn = Mathf.Lerp(smoothedTurn, turnInput, kTurn);

        // The root only ever turns around the world's up axis; pitch is tracked
        // separately (and shown on the Visual child) so cameras on the root stay level.
        transform.Rotate(0f, smoothedTurn * turnSpeed * dt, 0f, Space.World);

        // --- Pitch (up/down): tilt or W / S, smoothed. ---
        float pitchInput = 0f;
        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed) pitchInput += 1f;
            if (keyboard.sKey.isPressed) pitchInput -= 1f;
        }
        if (JoyconReady)
            pitchInput += joycon.Pitch;
        pitchInput = Mathf.Clamp(pitchInput, -1f, 1f);

        float targetPitch = pitchInput >= 0f ? pitchInput * maxClimbAngle : pitchInput * maxDiveAngle;
        float kPitch = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, pitchSmoothing));
        pitchAngle = Mathf.Lerp(pitchAngle, targetPitch, kPitch);

        // --- Movement: swim along the nose direction (horizontal heading plus pitch). ---
        float pitchRad = pitchAngle * Mathf.Deg2Rad;
        Vector3 direction = HorizontalForward() * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad);

        Vector3 p = transform.position + direction * CurrentSpeed * dt;
        p.y = Mathf.Max(seabedY, p.y); // never below the seabed
        transform.position = p;

        UpdateVisual(smoothedTurn);
    }

    private void UpdateSurface()
    {
        float dt = Time.deltaTime;

        // Level out and coast forward slowly for the arrival moment.
        boostRemaining = Mathf.Max(0f, boostRemaining - dt);
        pitchAngle = Mathf.MoveTowards(pitchAngle, 0f, 30f * dt);
        smoothedTurn = Mathf.MoveTowards(smoothedTurn, 0f, 2f * dt);

        CurrentSpeed = forwardSpeed * 0.5f;
        transform.position += HorizontalForward() * CurrentSpeed * dt;

        UpdateVisual(smoothedTurn);
    }

    private Vector3 HorizontalForward()
    {
        Vector3 f = transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    private void UpdateVisual(float turn)
    {
        if (visual == null) return;

        // Bank into the turn (right turn = roll right); the nose pitch matches the movement.
        // In Unity a NEGATIVE X rotation lifts the nose.
        float targetBank = -turn * maxBank;
        float k = 1f - Mathf.Exp(-visualSmoothing * Time.deltaTime);
        bank = Mathf.Lerp(bank, targetBank, k);

        visual.localRotation = Quaternion.Euler(-pitchAngle, 0f, bank);
    }
}