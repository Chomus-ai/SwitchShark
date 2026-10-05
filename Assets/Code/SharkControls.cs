using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Shark controller. Behaviour depends on GameManager's state:
///   Intro  : press the Joy-Con action button to start (Space also works via GameManager)
///   Rise   : tap the action button / Space repeatedly to gain height (sinks slowly if you stop)
///   Eat    : shark swims straight ahead; tilt the Joy-Con or press A / D to turn
///   Return : hold the Joy-Con still (or hold S) to sink back down to the seabed
/// Joy-Con input is used when a Joy-Con is connected; the keyboard always works too.
/// </summary>
public class SharkRise : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [Tooltip("Optional. Leave empty to use the keyboard only.")]
    [SerializeField] private JoyconInputProvider joycon;

    [Header("Rise (tap button / Space)")]
    [SerializeField] private float riseDistancePerPress = 2f;
    [Tooltip("Slow sink while the player isn't pressing. Set to 0 to disable.")]
    [SerializeField] private float idleSinkSpeed = 0.5f;

    [Header("Eat (tilt / A, D to turn)")]
    [SerializeField] private float forwardSpeed = 5f;
    [Tooltip("Degrees per second at full steering.")]
    [SerializeField] private float turnSpeed = 90f;

    [Header("Return (hold still / hold S)")]
    [SerializeField] private float sinkSpeed = 8f;
    [Tooltip("Snap back to the starting position/rotation (keeping current height) when Return begins.")]
    [SerializeField] private bool resetPoseOnReturn = true;

    // Captured at Start: the shark is assumed to begin resting on the seabed.
    private float seabedY;
    private Vector3 startPosition;
    private Quaternion startRotation;

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

        // Whatever way the player is holding the Joy-Con now counts as "straight ahead".
        if (next == GameState.Eat && joycon != null)
            joycon.CalibrateTilt();

        if (next == GameState.Return && resetPoseOnReturn)
        {
            // Back to the 2D side view pose, keeping the height the shark ended Eat at.
            Vector3 p = startPosition;
            p.y = transform.position.y;
            transform.SetPositionAndRotation(p, startRotation);
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
                break;

            case GameState.Rise:   UpdateRise(keyboard);   break;
            case GameState.Eat:    UpdateEat(keyboard);    break;
            case GameState.Return: UpdateReturn(keyboard); break;
        }
    }

    private void UpdateRise(Keyboard keyboard)
    {
        bool tap = (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                   || (JoyconReady && joycon.ActionPressedThisFrame);

        if (tap)
            ChangeHeight(riseDistancePerPress);

        if (idleSinkSpeed > 0f)
            ChangeHeight(-idleSinkSpeed * Time.deltaTime);
    }

    private void UpdateEat(Keyboard keyboard)
    {
        float turn = 0f;
        if (keyboard != null)
        {
            if (keyboard.dKey.isPressed) turn += 1f;
            if (keyboard.aKey.isPressed) turn -= 1f;
        }
        if (JoyconReady)
            turn += joycon.Steer;

        turn = Mathf.Clamp(turn, -1f, 1f);

        // Turn around the world's up axis so the shark never pitches or rolls.
        transform.Rotate(0f, turn * turnSpeed * Time.deltaTime, 0f, Space.World);

        // Always advance along the horizontal part of the shark's forward direction.
        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.0001f)
            transform.position += forward.normalized * forwardSpeed * Time.deltaTime;
    }

    private void UpdateReturn(Keyboard keyboard)
    {
        // 0 = no descent, 1 = full descent speed.
        float sink = 0f;
        if (keyboard != null && keyboard.sKey.isPressed) sink = 1f;
        if (JoyconReady) sink = Mathf.Max(sink, joycon.Stillness);

        ChangeHeight(-sinkSpeed * sink * Time.deltaTime);
    }

    private void ChangeHeight(float delta)
    {
        Vector3 p = transform.position;
        p.y = Mathf.Max(seabedY, p.y + delta);
        transform.position = p;
    }
}
