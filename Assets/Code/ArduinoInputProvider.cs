using UnityEngine;

/// <summary>
/// Arduino-controller version of JoyconInputProvider. Same public API, so game
/// scripts that use ControllerInputProvider work with either:
///   ActionPressedThisFrame : airbag squeeze starts (start game, Rise taps)
///   Steer                  : -1..1 from the IMU (Eat)
///   Pitch                  : -1..1 from the IMU tilt down/up (Eat)
///   Stillness              : 0..1, 1 = held perfectly still (Return)
///   RockedThisFrame        : one side-to-side rock (Rise)
///
/// Everything is measured in DEGREES (the Joy-Con version used g of acceleration).
/// Requires an ArduinoController in the scene (or assign one below).
/// Deadzones/ranges are tuned here in the Inspector - no need to re-flash the Arduino.
/// </summary>
[DefaultExecutionOrder(-90)] // after ArduinoController (-100), before ordinary scripts
public class ArduinoInputProvider : ControllerInputProvider
{
    public enum AngleSource { Yaw, Roll }

    [Header("Source")]
    [Tooltip("Leave empty to use ArduinoController.Instance.")]
    [SerializeField] private ArduinoController arduino;

    [Header("Steering (Eat)")]
    [Tooltip("Yaw = swing the controller left/right like a pointer (what your original sketch used; can drift over time). " +
             "Roll = tilt it sideways like the Joy-Con (gravity-referenced, so it doesn't drift). " +
             "Which one matches a sideways tilt depends on how the IMU is mounted - check with Log Values.")]
    [SerializeField] private AngleSource steerSource = AngleSource.Yaw;
    [Tooltip("Degrees from neutral that count as full steering.")]
    [SerializeField] private float steerRange = 45f;
    [SerializeField] private float steerDeadzone = 5f;
    [SerializeField] private bool invertSteer = false;

    [Header("Pitch (tilt up / down)")]
    [Tooltip("Degrees from neutral that count as full up / full down.")]
    [SerializeField] private float pitchRange = 30f;
    [SerializeField] private float pitchDeadzone = 5f;
    [Tooltip("Tick if tilting UP gives a negative value.")]
    [SerializeField] private bool invertPitch = false;

    [Header("Stillness (Return)")]
    [Tooltip("Motion (deg/s) at or below this counts as perfectly still. Starting guess - tune with Log Values.")]
    [SerializeField] private float stillThreshold = 4f;
    [Tooltip("Motion (deg/s) at or above this counts as moving. Starting guess - tune with Log Values.")]
    [SerializeField] private float moveThreshold = 30f;
    [Tooltip("0-1. Higher = reacts faster but is noisier.")]
    [SerializeField, Range(0f, 1f)] private float motionSmoothing = 0.2f;
    [Tooltip("If ticked, the airbag must be held squeezed for stillness to count " +
             "(stops a controller resting on a table counting as 'held still').")]
    [SerializeField] private bool requireButtonHeldForStillness = false;

    [Header("Rock to rise (Rise)")]
    [Tooltip("Degrees the steering angle must swing away from its slow-moving average to count as one rock. " +
             "15 deg is roughly equivalent to the Joy-Con's 0.25 g.")]
    [SerializeField] private float rockThreshold = 15f;
    [Tooltip("Seconds the 'resting angle' takes to follow slow movements. Slow tilts then don't count as rocks.")]
    [SerializeField] private float baselineTime = 1.0f;

    [Header("Debug")]
    [SerializeField] private bool logValues = false;

    private float neutralSteerAngle;
    private float neutralPitchAngle;
    private float smoothedMotion;

    private float tiltBaseline;
    private int rockSide;
    private bool rocked;

    private bool wasReady;
    private float logTimer;

    private ArduinoController Src => arduino != null ? arduino : ArduinoController.Instance;

    private bool Ready
    {
        get
        {
            ArduinoController s = Src;
            return s != null && s.IsConnected && s.ImuReady;
        }
    }

    /// <summary>True while the Arduino is sending data.</summary>
    public override bool Connected
    {
        get
        {
            ArduinoController s = Src;
            return s != null && s.IsConnected;
        }
    }

    /// <summary>True for exactly one frame each time a squeeze starts.</summary>
    public override bool ActionPressedThisFrame
    {
        get
        {
            ArduinoController s = Src;
            return s != null && s.IsConnected && s.ActionDown;
        }
    }

    /// <summary>True for one frame each time the controller is rocked from one side to the other.</summary>
    public override bool RockedThisFrame => rocked;

    private float RawSteerAngle
    {
        get
        {
            ArduinoController s = Src;
            if (s == null) return 0f;
            return steerSource == AngleSource.Yaw ? s.YawDeg : s.RollDeg;
        }
    }

    private float RawPitchAngle
    {
        get
        {
            ArduinoController s = Src;
            return s == null ? 0f : s.PitchDeg;
        }
    }

    // DeltaAngle keeps the difference correct across the +/-180 degree wrap.
    private float SteerFromNeutral => Mathf.DeltaAngle(neutralSteerAngle, RawSteerAngle);
    private float PitchFromNeutral => Mathf.DeltaAngle(neutralPitchAngle, RawPitchAngle);

    public override float Steer
    {
        get
        {
            if (!Ready) return 0f;
            float t = SteerFromNeutral;
            if (Mathf.Abs(t) < steerDeadzone) return 0f;
            float s = Mathf.Clamp(t / steerRange, -1f, 1f);
            return invertSteer ? -s : s;
        }
    }

    /// <summary>-1 (tilted fully down) to +1 (tilted fully up), relative to the neutral hold.</summary>
    public override float Pitch
    {
        get
        {
            if (!Ready) return 0f;
            float t = PitchFromNeutral;
            if (Mathf.Abs(t) < pitchDeadzone) return 0f;
            float p = Mathf.Clamp(t / pitchRange, -1f, 1f);
            return invertPitch ? -p : p;
        }
    }

    public override float Stillness
    {
        get
        {
            if (!Ready) return 0f;
            ArduinoController s = Src;
            if (requireButtonHeldForStillness && !s.ActionHeld) return 0f;
            return 1f - Mathf.InverseLerp(stillThreshold, moveThreshold, smoothedMotion);
        }
    }

    /// <summary>Call when Eat begins so the player's current hold counts as 'straight ahead'.</summary>
    public override void CalibrateTilt()
    {
        neutralSteerAngle = RawSteerAngle;
        neutralPitchAngle = RawPitchAngle;
    }

    /// <summary>Call when Rise begins so earlier tilt history doesn't count.</summary>
    public override void ResetRock()
    {
        tiltBaseline = SteerFromNeutral;
        rockSide = 0;
        rocked = false;
    }

    private void Update()
    {
        rocked = false;

        bool ready = Ready;

        // First valid frame (or the Arduino finished re-calibrating): treat the
        // current hold as "straight ahead", like the Joy-Con provider does on connect.
        if (ready && !wasReady)
        {
            CalibrateTilt();
            ResetRock();
        }
        wasReady = ready;

        if (!ready)
        {
            smoothedMotion = 0f;
            return;
        }

        smoothedMotion = Mathf.Lerp(smoothedMotion, Src.MotionDps, motionSmoothing);

        UpdateRock();

        if (logValues)
        {
            logTimer -= Time.deltaTime;
            if (logTimer <= 0f)
            {
                logTimer = 0.25f;
                ArduinoController s = Src;
                Debug.Log($"[Arduino] yaw={s.YawDeg:F1} pitch={s.PitchDeg:F1} roll={s.RollDeg:F1} " +
                          $"steerFromNeutral={SteerFromNeutral:F1} motion={smoothedMotion:F1}deg/s " +
                          $"steer={Steer:F2} pitchOut={Pitch:F2} stillness={Stillness:F2} action={s.ActionHeld}");
            }
        }
    }

    private void UpdateRock()
    {
        float tilt = SteerFromNeutral;

        // Slow-moving average of the angle = where the controller "rests".
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
            Debug.Log("[Arduino] Rock detected");
    }
}
