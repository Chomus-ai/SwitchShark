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

    [Header("Debug")]
    [SerializeField] private bool debugDisplay = false;
    [Tooltip("Log the '#' status messages the Arduino prints (calibration done, ready to squeeze, ...).")]
    [SerializeField] private bool logArduinoMessages = true;

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

        running = true;
        readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ArduinoController serial reader" };
        readThread.Start();
    }

    private void OnDisable()
    {
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
            Debug.Log("[ArduinoController] Opened " + name + " @ " + configuredBaud +
                      " (opening the port resets an Uno - expect ~2 s before data flows)");
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

    /// <summary>
    /// Asks the Arduino to re-measure its absolute neutral pose (takes ~30 IMU samples;
    /// ImuReady is false meanwhile). You normally DON'T need this: ArduinoInputProvider.CalibrateTilt()
    /// already re-zeroes instantly on the Unity side. Use it only to recover from heavy drift.
    /// </summary>
    public void RequestCalibration()
    {
        SerialPort p = port;
        try
        {
            if (p != null && p.IsOpen) p.Write("C");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ArduinoController] Could not send calibration request: " + e.Message);
        }
    }

    private void OnGUI()
    {
        if (!debugDisplay) return;
        GUI.Label(new Rect(10, 30, 900, 20),
            $"Arduino  connected:{IsConnected}  imuReady:{ImuReady}  yaw:{YawDeg:F1}  pitch:{PitchDeg:F1}  roll:{RollDeg:F1}  motion:{MotionDps:F1}deg/s  action:{ActionHeld}");
    }
}