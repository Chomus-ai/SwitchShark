using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Shark controller for the rising-only game. Behaviour depends on GameManager's state:
///   Intro   : press the Joy-Con action button to start (Space also works via GameManager)
///   Rise    : the shark swims forward automatically. Tilt the Joy-Con (or A / D) to turn.
///             Each button press (or Space) raises the shark; it sinks slowly if you stop.
///   Surface : the shark glides forward slowly, no input.
/// Joy-Con input is used when a Joy-Con is connected; the keyboard always works too.
/// </summary>
public class SharkRise : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [Tooltip("Optional. Leave empty to use the keyboard only.")]
    [SerializeField] private JoyconInputProvider joycon;
    [Tooltip("Optional. A child object holding the shark MODEL (with identity rotation). " +
             "It is pitched up while rising and banked into turns. Leave empty to skip.")]
    [SerializeField] private Transform visual;

    [Header("Rise (tap button / Space)")]
    [Tooltip("How much each press raises the shark's target height.")]
    [SerializeField] private float riseDistancePerPress = 2f;
    [Tooltip("Debounce: minimum seconds between presses that count. Presses sooner than this after " +
             "the last counted one are ignored, so spamming the button can't raise the shark faster. 0 = off.")]
    [SerializeField] private float minPressInterval = 0.25f;
    [Tooltip("Target height lost per second when the player isn't pressing. 0 = never sinks.")]
    [SerializeField] private float idleSinkSpeed = 0.3f;
    [Tooltip("Higher = slower, smoother climb toward the target height.")]
    [SerializeField] private float riseSmoothTime = 0.6f;

    [Header("Forward and steering (tilt / A, D)")]
    [SerializeField] private float forwardSpeed = 5f;
    [Tooltip("Degrees per second at full tilt.")]
    [SerializeField] private float turnSpeed = 60f;
    [Tooltip("Seconds of smoothing on steering. Hides jitter from pressing the button while tilting.")]
    [SerializeField] private float turnSmoothing = 0.15f;

    [Header("Visual flair (only used if 'Visual' is assigned)")]
    [Tooltip("Degrees of roll at full turn. Flip the sign if it leans the wrong way.")]
    [SerializeField] private float maxBank = 25f;
    [Tooltip("Degrees of nose-up at the climb speed below.")]
    [SerializeField] private float maxPitch = 15f;
    [Tooltip("Climb speed (units per second) that gives the full nose-up angle.")]
    [SerializeField] private float riseSpeedForMaxPitch = 3f;
    [SerializeField] private float visualSmoothing = 6f;

    [Header("Pose")]
    [Tooltip("Snap back to the starting position and rotation when returning to the Intro.")]
    [SerializeField] private bool resetPoseOnIntro = true;

    // Captured at Start: the shark is assumed to begin resting on the seabed.
    private float seabedY;
    private Vector3 startPosition;
    private Quaternion startRotation;

    private float targetHeight;
    private float heightVelocity;
    private float lastAcceptedPressTime = float.NegativeInfinity;
    private float smoothedTurn;
    private float bank;
    private float pitch;

    // Ignore input on the frame a state changes, so the press that starts
    // the game (Intro -> Rise) doesn't also count as the first rise tap.
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
        targetHeight = seabedY;
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
            targetHeight = transform.position.y;
            heightVelocity = 0f;
            smoothedTurn = 0f;
            lastAcceptedPressTime = float.NegativeInfinity;

            // Whatever way the player is holding the Joy-Con now counts as "straight ahead".
            if (joycon != null) joycon.CalibrateTilt();
        }

        if (next == GameState.Intro && resetPoseOnIntro)
        {
            transform.SetPositionAndRotation(startPosition, startRotation);
            targetHeight = startPosition.y;
            heightVelocity = 0f;
            smoothedTurn = 0f;
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
                UpdateVisual(0f, 0f);
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

        // --- Rising: each press raises the target; the shark eases up toward it. ---
        bool tap = (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                   || (JoyconReady && joycon.ActionPressedThisFrame);

        // Debounce: ignore presses that come too soon after the last counted one.
        if (tap && Time.time - lastAcceptedPressTime >= minPressInterval)
        {
            targetHeight += riseDistancePerPress;
            lastAcceptedPressTime = Time.time;
        }

        targetHeight = Mathf.Max(seabedY, targetHeight - idleSinkSpeed * dt);

        Vector3 p = transform.position;
        p.y = Mathf.SmoothDamp(p.y, targetHeight, ref heightVelocity, riseSmoothTime);

        // --- Steering: tilt (or A / D), smoothed. ---
        float turnInput = 0f;
        if (keyboard != null)
        {
            if (keyboard.dKey.isPressed) turnInput += 1f;
            if (keyboard.aKey.isPressed) turnInput -= 1f;
        }
        if (JoyconReady)
            turnInput += joycon.Steer;
        turnInput = Mathf.Clamp(turnInput, -1f, 1f);

        float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, turnSmoothing));
        smoothedTurn = Mathf.Lerp(smoothedTurn, turnInput, k);

        // Turn around the world's up axis so the root never pitches or rolls
        // (pitch and bank are applied to the Visual child only).
        transform.Rotate(0f, smoothedTurn * turnSpeed * dt, 0f, Space.World);

        // --- Forward: always advance along the horizontal part of the facing direction. ---
        p += HorizontalForward() * forwardSpeed * dt;
        transform.position = p;

        UpdateVisual(smoothedTurn, heightVelocity);
    }

    private void UpdateSurface()
    {
        // Just coast forward slowly for the arrival moment.
        transform.position += HorizontalForward() * (forwardSpeed * 0.5f) * Time.deltaTime;
        smoothedTurn = Mathf.MoveTowards(smoothedTurn, 0f, 2f * Time.deltaTime);
        UpdateVisual(smoothedTurn, 0f);
    }

    private Vector3 HorizontalForward()
    {
        Vector3 f = transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    private void UpdateVisual(float turn, float verticalSpeed)
    {
        if (visual == null) return;

        // Bank into the turn (right turn = roll right) and pitch the nose up while climbing.
        float targetBank = -turn * maxBank;
        float targetPitch = -Mathf.Clamp(verticalSpeed / Mathf.Max(0.01f, riseSpeedForMaxPitch), -1f, 1f) * maxPitch;

        float k = 1f - Mathf.Exp(-visualSmoothing * Time.deltaTime);
        bank = Mathf.Lerp(bank, targetBank, k);
        pitch = Mathf.Lerp(pitch, targetPitch, k);

        visual.localRotation = Quaternion.Euler(pitch, 0f, bank);
    }
}