using System;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using UnityEngine;

/// <summary>
/// Transport layer for the Arduino controller (sketch_3301CONTROLLER.ino) - the
/// equivalent of JoyconManager. It owns the USB serial port and exposes the raw
/// readings. The game-facing meaning (Steer, Pitch, Stillness, Rock) is worked
/// out in ArduinoInputProvider, mirroring JoyconInputProvider.
///
/// Requires: Edit > Project Settings > Player > Other Settings >
///           Api Compatibility Level = ".NET Framework" (System.IO.Ports),
///           Scripting Backend = Mono (recommended for serial on macOS).
///
/// Port selection (works on Windows and macOS):
///   1. If Port Name is set AND that port currently exists, it is used.
///   2. Otherwise the port is auto-detected (usbmodem / usbserial / wchusbserial
///      on macOS, first COM port on Windows).
///   Leave Port Name empty to always auto-detect.
///
/// Session restart on connect:
///   Opening the port resets an Arduino Uno on some systems (DTR toggles) and not on
///   others. To get the same result everywhere, the sketch prints "# BOOT" when it
///   starts. If no "# BOOT" arrives within RestartCheckDelaySeconds of opening the
///   port, this script sends 'R' so the Arduino re-calibrates its IMU and re-inflates
///   / re-settles the airbag, exactly as if it had just been reset.
///
/// Testable in isolation: put this on an empty GameObject, tick Debug Display
/// and press Play. No other script is needed.
///
/// The serial read runs on a background thread so a slow/blocked port can never
/// freeze the game. If the cable is unplugged it retries every 2 seconds, and
/// all outputs fall back to zero/false.
/// </summary>
[DefaultExecutionOrder(-100)] // run before other scripts so they read this frame's values
public class ArduinoController : MonoBehaviour
{
    public static ArduinoController Instance { get; private set; }

    [Header("Serial")]
    [Tooltip("Optional override. Windows: COM3, COM4... macOS: /dev/cu.usbmodemXXXX. " +
             "Leave EMPTY (or if the name isn't present) to auto-detect. Available ports are logged on Play.")]
    [SerializeField] private string portName = "";
    [Tooltip("Must match Serial.begin() in the Arduino sketch (115200).")]
    [SerializeField] private int baudRate = 115200;

    [Header("Session restart")]
    [Tooltip("After opening the port, wait this long for the Arduino's '# BOOT' message. " +
             "If it doesn't arrive (the board didn't reset), send 'R' to restart the session. " +
             "Must be longer than the bootloader delay (~2 s on an Uno).")]
    [SerializeField] private float restartCheckDelaySeconds = 3f;
    [Tooltip("Turn off to never send the automatic restart command.")]
    [SerializeField] private bool restartOnConnect = true;

    [Header("Debug")]
    [SerializeField] private bool debugDisplay = false;
    [Tooltip("Log the '#' status messages the Arduino prints (calibration done, ready to squeeze, ...).")]
    [SerializeField] private bool logArduinoMessages = true;

    [Header("Per-player calibration")]
    [Tooltip("Drag the GameManager here. Each time the game enters Intro (a new player has just squeezed), " +
         "the Arduino re-measures its neutral pose.")]
    [SerializeField] private GameManager gameManager;
    [SerializeField] private bool recalibrateOnIntro = true;

    // ---- Latest values (refreshed once per frame in Update) ----
    /// <summary>Angles in degrees relative to the Arduino's neutral pose.</summary>
    public float YawDeg { get; private set; }
    public float PitchDeg { get; private set; }   // positive = tilted up
    public float RollDeg { get; private set; }
    /// <summary>How much the controller is moving, in degrees/second (averaged over ~200 ms).</summary>
    public float MotionDps { get; private set; }

    public bool ActionHeld { get; private set; }  // airbag currently squeezed
    public bool ActionDown { get; private set; }  // true for exactly ONE frame when a squeeze starts
    public bool ActionUp { get; private set; }

    public bool IsConnected { get; private set; } // valid frame received within the last second
    public bool ImuReady { get; private set; }    // false while the Arduino calibrates its neutral pose

    public event Action OnActionDown;
    public event Action OnActionUp;

    private SerialPort port;
    private Thread readThread;
    private volatile bool running;

    // Latest parsed frame, written by the reader thread, read by Update.
    private readonly object frameLock = new object();
    private float rawYaw, rawPitch, rawRoll, rawMotion;
    private bool rawHeld, rawReady;
    private long lastFrameTicks;

    private bool previousHeld;

    // Copied on the main thread so the reader thread never touches serialized fields mid-edit.
    private string configuredPort;
    private int configuredBaud;
    private float configuredRestartDelay;
    private bool configuredRestartOnConnect;

    // Restart-on-connect state (reader thread only, except the volatile flag).
    private volatile bool sawBootMarker;
    private bool restartCheckPending;
    private long openedTicks;

    // The reader thread and the main thread can both write to the port.
    private readonly object writeLock = new object();

    // ------------------------------------------------------------------
    // Port selection
    // ------------------------------------------------------------------

    /// <summary>
    /// Picks the serial port to open. Called from the reader thread on every (re)connect
    /// attempt, so an Arduino plugged in later or on a different USB port is still found.
    /// </summary>
    private static string ResolvePort(string preferred)
    {
        string[] ports;
        try { ports = SerialPort.GetPortNames(); }
        catch (Exception e)
        {
            Debug.LogWarning("[ArduinoController] Could not list serial ports: " + e.Message);
            return null;
        }

        // 1. Honour the manual override, but only if that port actually exists right now.
        if (!string.IsNullOrWhiteSpace(preferred) && ports.Contains(preferred))
            return preferred;

        // 2. macOS / Linux: prefer the call-out device (/dev/cu.*). The /dev/tty.* twin can
        //    block while waiting for a carrier signal, so it is skipped.
        string unixPort = ports.FirstOrDefault(p =>
            p.StartsWith("/dev/cu.", StringComparison.Ordinal) &&
            (p.Contains("usbmodem") || p.Contains("usbserial") || p.Contains("wchusbserial")));
        if (unixPort != null) return unixPort;

        // 3. Windows: first COM port.
        return ports.FirstOrDefault(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase));
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        Debug.Log("[ArduinoController] Available ports: " + string.Join(", ", SerialPort.GetPortNames()));
    }

    private void OnEnable()
    {
        configuredPort = portName;
        configuredBaud = baudRate;
        configuredRestartDelay = Mathf.Max(0.5f, restartCheckDelaySeconds);
        configuredRestartOnConnect = restartOnConnect;

        running = true;
        if (gameManager != null) gameManager.StateChanged += HandleGameStateChanged;
        readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ArduinoController serial reader" };
        readThread.Start();
    }

    private void OnDisable()
    {
        if (gameManager != null) gameManager.StateChanged -= HandleGameStateChanged;
        running = false;
        if (readThread != null)
        {
            readThread.Join(1000);
            readThread = null;
        }
    }

    private void OnApplicationQuit()
    {
        // Make sure the port is released so the next launch (or the Arduino IDE) can open it.
        running = false;
        if (readThread != null) readThread.Join(1000);
        SafeClose();
    }

    private void HandleGameStateChanged(GameState previous, GameState next)
    {
        if (recalibrateOnIntro && next == GameState.Intro)
            RequestCalibration();   // sends 'C' to the Arduino
    }

    // ------------------------------------------------------------------
    // Background thread
    // ------------------------------------------------------------------
    private void ReadLoop()
    {
        while (running)
        {
            if (port == null || !port.IsOpen)
            {
                if (!TryOpen())
                {
                    // Wait ~2 s before retrying, but wake up quickly if we're shutting down.
                    for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);
                    continue;
                }
            }

            // Restart-on-connect: if the board did not reboot by itself when the port
            // opened, ask it to restart its session. (ReadLine times out every 100 ms,
            // so this check runs often enough.)
            if (restartCheckPending &&
                (System.Diagnostics.Stopwatch.GetTimestamp() - openedTicks) >=
                    configuredRestartDelay * System.Diagnostics.Stopwatch.Frequency)
            {
                restartCheckPending = false;
                if (!sawBootMarker)
                {
                    Debug.Log("[ArduinoController] Board did not reboot on connect - sending restart command.");
                    SendCommand("R");
                }
            }

            try
            {
                ParseLine(port.ReadLine());
            }
            catch (TimeoutException)
            {
                // Nothing arrived within ReadTimeout - normal, just loop again.
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ArduinoController] Serial error: " + e.Message + " (will retry)");
                SafeClose();
            }
        }

        SafeClose();
    }

    private bool TryOpen()
    {
        string name = ResolvePort(configuredPort);
        if (name == null)
        {
            // Nothing plugged in yet - stay quiet-ish and retry.
            Debug.LogWarning("[ArduinoController] No Arduino serial port found (retrying every 2 s).");
            return false;
        }

        try
        {
            port = new SerialPort(name, configuredBaud)
            {
                ReadTimeout = 100,
                WriteTimeout = 100,
                NewLine = "\n"
            };
            port.Open();
            port.DiscardInBuffer();

            sawBootMarker = false;
            restartCheckPending = configuredRestartOnConnect;
            openedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            Debug.Log("[ArduinoController] Opened " + name + " @ " + configuredBaud +
                      " (the board may reset on open; if not, a restart command is sent after " +
                      configuredRestartDelay.ToString("0.#", CultureInfo.InvariantCulture) + " s)");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ArduinoController] Could not open " + name + ": " + e.Message);
            SafeClose();
            return false;
        }
    }

    private void SafeClose()
    {
        try { if (port != null && port.IsOpen) port.Close(); } catch { }
        port = null;
    }

    // Expected frame:  D,<yawDeg>,<pitchDeg>,<rollDeg>,<squeezing>,<imuReady>,<motionDps>
    private void ParseLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;

        if (line[0] == '#')
        {
            // The sketch prints this once on every (re)boot.
            if (line.StartsWith("# BOOT", StringComparison.Ordinal)) sawBootMarker = true;

            if (logArduinoMessages) Debug.Log("[Arduino] " + line);
            return;
        }

        if (!line.StartsWith("D,")) return;

        string[] f = line.Split(',');
        if (f.Length < 7) return;

        // InvariantCulture: Arduino always prints '.', but a PC set to a
        // comma-decimal locale would otherwise fail to parse "0.35".
        const NumberStyles ns = NumberStyles.Float;
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (!float.TryParse(f[1], ns, ci, out float yaw)) return;
        if (!float.TryParse(f[2], ns, ci, out float pitch)) return;
        if (!float.TryParse(f[3], ns, ci, out float roll)) return;
        if (!float.TryParse(f[6], ns, ci, out float motion)) return;

        bool held = f[4] == "1";
        bool ready = f[5] == "1";

        lock (frameLock)
        {
            rawYaw = yaw;
            rawPitch = pitch;
            rawRoll = roll;
            rawMotion = motion;
            rawHeld = held;
            rawReady = ready;
            lastFrameTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    // ------------------------------------------------------------------
    // Main thread
    // ------------------------------------------------------------------
    private void Update()
    {
        float yaw, pitch, roll, motion;
        bool held, ready;
        long last;

        lock (frameLock)
        {
            yaw = rawYaw;
            pitch = rawPitch;
            roll = rawRoll;
            motion = rawMotion;
            held = rawHeld;
            ready = rawReady;
            last = lastFrameTicks;
        }

        // "Connected" = a valid frame arrived within the last second.
        bool connected = last != 0 &&
            (System.Diagnostics.Stopwatch.GetTimestamp() - last) < System.Diagnostics.Stopwatch.Frequency;

        IsConnected = connected;
        ImuReady = connected && ready;

        // Fail-safe: no connection or not calibrated -> zeros.
        YawDeg = ImuReady ? yaw : 0f;
        PitchDeg = ImuReady ? pitch : 0f;
        RollDeg = ImuReady ? roll : 0f;
        MotionDps = ImuReady ? motion : 0f;

        held = connected && held;
        ActionHeld = held;
        ActionDown = held && !previousHeld;
        ActionUp = !held && previousHeld;
        previousHeld = held;

        if (ActionDown) OnActionDown?.Invoke();
        if (ActionUp) OnActionUp?.Invoke();
    }

    // ------------------------------------------------------------------
    // Commands to the Arduino
    // ------------------------------------------------------------------

    /// <summary>
    /// Thread-safe write of a single-character command. Called from both the reader
    /// thread (automatic restart) and the main thread (public methods below).
    /// </summary>
    private void SendCommand(string cmd)
    {
        SerialPort p = port;
        try
        {
            lock (writeLock)
            {
                if (p != null && p.IsOpen) p.Write(cmd);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ArduinoController] Could not send '" + cmd + "': " + e.Message);
        }
    }

    /// <summary>
    /// Asks the Arduino to re-measure its absolute neutral pose (takes ~50 IMU samples;
    /// ImuReady is false meanwhile). You normally DON'T need this: ArduinoInputProvider.CalibrateTilt()
    /// already re-zeroes instantly on the Unity side. Use it only to recover from heavy drift.
    /// </summary>
    public void RequestCalibration() => SendCommand("C");

    /// <summary>
    /// Restarts the Arduino session: re-calibrates the IMU AND re-inflates / re-settles the
    /// airbag. The controller must be held still for ~1-2 s afterwards. Sent automatically
    /// on connect when the board did not reboot by itself.
    /// </summary>
    public void RequestRestart() => SendCommand("R");

    private void OnGUI()
    {
        if (!debugDisplay) return;
        GUI.Label(new Rect(10, 30, 900, 20),
            $"Arduino  connected:{IsConnected}  imuReady:{ImuReady}  yaw:{YawDeg:F1}  pitch:{PitchDeg:F1}  roll:{RollDeg:F1}  motion:{MotionDps:F1}deg/s  action:{ActionHeld}");
    }
}