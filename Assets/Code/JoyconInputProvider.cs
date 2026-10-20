using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Wraps JoyconLib and turns raw Joy-Con data into the three inputs the game needs:
///   ActionPressedThisFrame : button press (start game, Rise taps)
///   Steer                  : -1..1 from tilting the controller (Eat)
///   Stillness              : 0..1, 1 = held perfectly still (Return)
///
/// Requires JoyconLib's JoyconManager to be on a GameObject in the scene.
/// Verify the JoyconLib calls used here (GetButton, GetButtonDown, GetAccel)
/// against JoyconDemo.cs in your copy of the library.
/// </summary>
public class JoyconInputProvider : MonoBehaviour
{
    [Header("Which Joy-Con")]
    [Tooltip("Tick for the left Joy-Con, untick for the right one.")]
    [SerializeField] private bool useLeftJoycon = false;

    [Header("Action button (start + Rise taps)")]
    [SerializeField] private Joycon.Button actionButton = Joycon.Button.SHOULDER_2;

    [Header("Steering (Eat)")]
    [Tooltip("Which accelerometer axis changes when you tilt left/right. " +
             "Use the debug log to find it, then set it to e.g. (1,0,0), (0,1,0) or (0,-1,0).")]
    [SerializeField] private Vector3 tiltAxis = new Vector3(0f, 1f, 0f);
    [Tooltip("Tilt (in g) from neutral that counts as full steering.")]
    [SerializeField] private float tiltRange = 0.5f;
    [SerializeField] private float tiltDeadzone = 0.05f;
    [SerializeField] private bool invertSteer = false;

    [Header("Pitch (tilt up / down)")]
    [Tooltip("Which accelerometer axis changes when you tilt the front of the controller up/down. " +
             "Must be a different axis from the steering one. Find it with Log Values.")]
    [SerializeField] private Vector3 pitchAxis = new Vector3(1f, 0f, 0f);
    [Tooltip("Tilt (in g) from neutral that counts as full up / full down.")]
    [SerializeField] private float pitchRange = 0.5f;
    [SerializeField] private float pitchDeadzone = 0.05f;
    [Tooltip("Tick if tilting UP gives a negative reading.")]
    [SerializeField] private bool invertPitch = false;

    [Header("Stillness (Return)")]
    [Tooltip("Motion at or below this counts as perfectly still (full descent).")]
    [SerializeField] private float stillThreshold = 0.02f;
    [Tooltip("Motion at or above this counts as moving (no descent).")]
    [SerializeField] private float moveThreshold = 0.10f;
    [Tooltip("0-1. Higher = reacts faster but is noisier.")]
    [SerializeField] private float motionSmoothing = 0.2f;
    [Tooltip("If ticked, the action button must be held for stillness to count. " +
             "Stops a controller resting on a table from counting as 'held still'.")]
    [SerializeField] private bool requireButtonHeldForStillness = false;

    [Header("Rock to rise (Rise)")]
    [Tooltip("How far (in g) the tilt must swing away from its slow-moving average to count as one rock. Lower = more sensitive.")]
    [SerializeField] private float rockThreshold = 0.25f;
    [Tooltip("Seconds the 'resting tilt' takes to follow slow movements. Slow tilts then don't count as rocks.")]
    [SerializeField] private float baselineTime = 1.0f;

    [Header("Debug")]
    [SerializeField] private bool logValues = false;
    [Tooltip("Logs the name of every Joy-Con button as you press it. Use it to find the right Action Button.")]
    [SerializeField] private bool logButtonPresses = false;

    private Joycon joycon;
    private bool actionHeldPrev;
    private bool actionPressed;
    private bool[] prevButtons;

    private float tiltBaseline;
    private int rockSide;
    private bool rocked;
    private float retryTimer;

    private Vector3 prevAccel;
    private bool hasPrevAccel;
    private float smoothedMotion;
    private float neutralTilt;
    private float neutralPitch;
    private float logTimer;

    /// <summary>True once a matching Joy-Con has been found.</summary>
    public bool Connected => joycon != null;

    /// <summary>True for exactly one frame each time the action button goes down.</summary>
    public bool ActionPressedThisFrame => actionPressed;

    /// <summary>
    /// True for one frame each time the controller is rocked from one side to the
    /// other (tilted past the threshold, then past it again the opposite way).
    /// </summary>
    public bool RockedThisFrame => rocked;

    /// <summary>Call when Rise begins so earlier tilt history doesn't count.</summary>
    public void ResetRock()
    {
        tiltBaseline = CurrentTilt;
        rockSide = 0;
        rocked = false;
    }

    public float Steer
    {
        get
        {
            if (joycon == null) return 0f;
            float t = CurrentTilt - neutralTilt;
            if (Mathf.Abs(t) < tiltDeadzone) return 0f;
            float s = Mathf.Clamp(t / tiltRange, -1f, 1f);
            return invertSteer ? -s : s;
        }
    }

    /// <summary>-1 (tilted fully down) to +1 (tilted fully up), relative to the neutral hold.</summary>
    public float Pitch
    {
        get
        {
            if (joycon == null) return 0f;
            float t = CurrentPitchTilt - neutralPitch;
            if (Mathf.Abs(t) < pitchDeadzone) return 0f;
            float p = Mathf.Clamp(t / pitchRange, -1f, 1f);
            return invertPitch ? -p : p;
        }
    }

    public float Stillness
    {
        get
        {
            if (joycon == null) return 0f;
            if (requireButtonHeldForStillness && !joycon.GetButton(actionButton)) return 0f;
            return 1f - Mathf.InverseLerp(stillThreshold, moveThreshold, smoothedMotion);
        }
    }

    private float CurrentTilt =>
        joycon == null ? 0f : Vector3.Dot(joycon.GetAccel(), tiltAxis.normalized);

    private float CurrentPitchTilt =>
        joycon == null ? 0f : Vector3.Dot(joycon.GetAccel(), pitchAxis.normalized);

    /// <summary>Call when Eat begins so the player's current hold counts as 'straight ahead'.</summary>
    public void CalibrateTilt()
    {
        neutralTilt = CurrentTilt;
        neutralPitch = CurrentPitchTilt;
    }

    private void Update()
    {
        if (joycon == null)
        {
            TryFindJoycon();
            return;
        }

        // Motion = how much the accelerometer reading changed since last frame.
        Vector3 accel = joycon.GetAccel();
        if (hasPrevAccel)
        {
            float delta = (accel - prevAccel).magnitude;
            smoothedMotion = Mathf.Lerp(smoothedMotion, delta, motionSmoothing);
        }
        prevAccel = accel;
        hasPrevAccel = true;

        // Detect presses from the held state so each press counts exactly once.
        bool actionHeld = joycon.GetButton(actionButton);
        actionPressed = actionHeld && !actionHeldPrev;
        actionHeldPrev = actionHeld;

        if (logButtonPresses) LogButtonPresses();

        UpdateRock();

        if (logValues)
        {
            logTimer -= Time.deltaTime;
            if (logTimer <= 0f)
            {
                logTimer = 0.25f;
                Debug.Log($"[Joycon] accel={accel} (|a|={accel.magnitude:F2}) " +
                          $"tilt={CurrentTilt:F2} pitchTilt={CurrentPitchTilt:F2} motion={smoothedMotion:F3} " +
                          $"steer={Steer:F2} pitch={Pitch:F2} stillness={Stillness:F2}");
            }
        }
    }

    private void UpdateRock()
    {
        rocked = false;
        float tilt = CurrentTilt;

        // Slow-moving average of the tilt = where the controller "rests".
        // Frame-rate independent smoothing, so slow tilts never count as rocks.
        float follow = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.05f, baselineTime));
        tiltBaseline = Mathf.Lerp(tiltBaseline, tilt, follow);

        float dev = tilt - tiltBaseline;
        int side = dev > rockThreshold ? 1 : (dev < -rockThreshold ? -1 : 0);

        if (side != 0 && side != rockSide)
        {
            // Reaching the opposite extreme from last time = one rock.
            // The very first extreme only arms the detector.
            if (rockSide != 0) rocked = true;
            rockSide = side;
        }

        if (rocked && logValues)
            Debug.Log("[Joycon] Rock detected");
    }

    private void LogButtonPresses()
    {
        Joycon.Button[] all = (Joycon.Button[])System.Enum.GetValues(typeof(Joycon.Button));
        if (prevButtons == null || prevButtons.Length != all.Length)
            prevButtons = new bool[all.Length];

        for (int i = 0; i < all.Length; i++)
        {
            bool held = joycon.GetButton(all[i]);
            if (held && !prevButtons[i])
                Debug.Log($"[Joycon] Button pressed: {all[i]}");
            prevButtons[i] = held;
        }
    }

    // JoyconManager may not have finished scanning when this script starts,
    // and the order of its Joy-Con list can change between runs, so look for the
    // right one by its isLeft flag, retrying twice a second until found.
    private void TryFindJoycon()
    {
        retryTimer -= Time.deltaTime;
        if (retryTimer > 0f) return;
        retryTimer = 0.5f;

        if (JoyconManager.Instance == null) return;

        List<Joycon> list = JoyconManager.Instance.j;
        if (list == null || list.Count == 0) return;

        joycon = list.Find(c => c.isLeft == useLeftJoycon);
        if (joycon != null)
        {
            Debug.Log($"[Joycon] Connected: {(useLeftJoycon ? "left" : "right")} Joy-Con.");
            CalibrateTilt();
        }
    }
}