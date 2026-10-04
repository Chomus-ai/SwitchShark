using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Keyboard test controller for the shark (new Input System). Behaviour depends on GameManager's state:
///   Rise   : tap Space repeatedly to gain height (it sinks slowly if you stop)
///   Eat    : shark swims straight ahead, A / D turn left / right
///   Return : hold S to sink back down to the seabed
/// Intended as a stand-in for the Wiimote controls.
/// </summary>
public class SharkRise : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;

    [Header("Rise (tap Space)")]
    [SerializeField] private float riseDistancePerPress = 2f;
    [Tooltip("Slow sink while the player isn't pressing. Set to 0 to disable.")]
    [SerializeField] private float idleSinkSpeed = 0.5f;

    [Header("Eat (A / D to turn)")]
    [SerializeField] private float forwardSpeed = 5f;
    [Tooltip("Degrees per second.")]
    [SerializeField] private float turnSpeed = 90f;

    [Header("Return (hold S)")]
    [SerializeField] private float sinkSpeed = 8f;
    [Tooltip("Snap back to the starting position/rotation (keeping current height) when Return begins.")]
    [SerializeField] private bool resetPoseOnReturn = true;

    // Captured at Start: the shark is assumed to begin resting on the seabed.
    private float seabedY;
    private Vector3 startPosition;
    private Quaternion startRotation;

    // Ignore input on the frame a state changes, so the Space press that starts
    // the game (Intro -> Rise) doesn't also count as the first rise tap.
    private int stateChangeFrame = -1;

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
        if (keyboard == null) return;

        switch (gameManager.CurrentState)
        {
            case GameState.Rise: UpdateRise(keyboard); break;
            case GameState.Eat: UpdateEat(keyboard); break;
            case GameState.Return: UpdateReturn(keyboard); break;
        }
    }

    private void UpdateRise(Keyboard keyboard)
    {
        if (keyboard.spaceKey.wasPressedThisFrame)
            ChangeHeight(riseDistancePerPress);

        if (idleSinkSpeed > 0f)
            ChangeHeight(-idleSinkSpeed * Time.deltaTime);

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.0001f)
            transform.position += forward.normalized * forwardSpeed * Time.deltaTime;
    }

    private void UpdateEat(Keyboard keyboard)
    {
        float turn = 0f;
        if (keyboard.dKey.isPressed) turn += 1f;
        if (keyboard.aKey.isPressed) turn -= 1f;

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
        if (keyboard.sKey.isPressed)
            ChangeHeight(-sinkSpeed * Time.deltaTime);

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.0001f)
            transform.position += forward.normalized * forwardSpeed * Time.deltaTime;
    }

    private void ChangeHeight(float delta)
    {
        Vector3 p = transform.position;
        p.y = Mathf.Max(seabedY, p.y + delta);
        transform.position = p;
    }
}