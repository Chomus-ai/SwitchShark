using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

public enum GameState { Intro, Rise, Eat, Return }

/// <summary>
/// Drives the game loop: Intro -> Rise -> Eat -> Return -> Intro.
///
/// The manager is passive: it reads values and decides WHEN to change state.
///   Rise   : shark height     >= maxHeight
///   Eat    : internal timer   <= 0
///   Return : shark height     <= minHeight + seabedTolerance
///
/// What happens on each change (cameras, UI, enabling controllers) is wired up
/// through the UnityEvents below or by subscribing to StateChanged.
/// </summary>
public class GameManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The shark. Its world Y position is used as its height.")]
    [SerializeField] private Transform shark;

    [Header("Height range (world Y)")]
    [Tooltip("Seabed. Return ends when the shark gets back down here.")]
    [SerializeField] private float minHeight = 0f;
    [Tooltip("Rise ends when the shark reaches this height.")]
    [SerializeField] private float maxHeight = 100f;
    [Tooltip("How close to minHeight counts as 'reached the seabed'.")]
    [SerializeField] private float seabedTolerance = 0.1f;

    [Header("Eat segment")]
    [SerializeField] private float eatDuration = 45f;

    [Header("State enter events (hook up cameras, UI, controllers here)")]
    [SerializeField] private UnityEvent onEnterIntro;
    [SerializeField] private UnityEvent onEnterRise;
    [SerializeField] private UnityEvent onEnterEat;
    [SerializeField] private UnityEvent onEnterReturn;

    [Header("Development")]
    [Tooltip("Keyboard fallback for starting the game from the Intro screen.")]
    [SerializeField] private Key debugStartKey = Key.Space;

    /// <summary>Fired after every state change: (previous, new).</summary>
    public event Action<GameState, GameState> StateChanged;

    public GameState CurrentState { get; private set; } = GameState.Intro;

    /// <summary>Shark height from 0 (seabed) to 1 (Rise target). Use this for the depth meter.</summary>
    public float NormalizedHeight => Mathf.InverseLerp(minHeight, maxHeight, CurrentHeight);

    /// <summary>Seconds left in the Eat segment (only meaningful during Eat).</summary>
    public float TimeRemaining => eatTimer;

    // If your Segment 1 controller keeps its own height value, read that here
    // instead (e.g. shark.GetComponent<SharkController>().Height) so there is
    // only ever one source of truth for the shark's height.
    private float CurrentHeight => shark.position.y;

    private float eatTimer;

    private void Awake()
    {
        if (shark == null)
            Debug.LogError("GameManager: 'shark' is not assigned.", this);
    }

    private void Start()
    {
        // First entry into Intro: there is no previous state to exit.
        EnterState(GameState.Intro);
    }

    private void Update()
    {
        if (shark == null) return;

        switch (CurrentState)
        {
            case GameState.Intro:
                Keyboard keyboard = Keyboard.current; // null if no keyboard is connected
                if (keyboard != null && keyboard[debugStartKey].wasPressedThisFrame)
                    RequestStart();
                break;

            case GameState.Rise:
                if (CurrentHeight >= maxHeight)
                    ChangeState(GameState.Eat);
                break;

            case GameState.Eat:
                eatTimer = Mathf.Max(0f, eatTimer - Time.deltaTime);
                if (eatTimer <= 0f)
                    ChangeState(GameState.Return);
                break;

            case GameState.Return:
                if (CurrentHeight <= minHeight + seabedTolerance)
                    ChangeState(GameState.Intro);
                break;
        }
    }

    // ---------- Public API (call from the input layer / Wiimote script) ----------

    /// <summary>Called when the player presses the start button on the Intro screen.</summary>
    public void RequestStart()
    {
        if (CurrentState == GameState.Intro)
            ChangeState(GameState.Rise);
    }

    /// <summary>Force back to Intro (e.g. from an inactivity timeout).</summary>
    public void ResetToIntro()
    {
        if (CurrentState != GameState.Intro)
            ChangeState(GameState.Intro);
    }

    // ---------- Internals ----------

    private void ChangeState(GameState next)
    {
        if (next == CurrentState) return;

        GameState previous = CurrentState;
        EnterState(next);
        StateChanged?.Invoke(previous, next);

        Debug.Log($"[GameManager] {previous} -> {next}");
    }

    private void EnterState(GameState state)
    {
        CurrentState = state;

        switch (state)
        {
            case GameState.Intro:
                onEnterIntro?.Invoke();
                break;

            case GameState.Rise:
                onEnterRise?.Invoke();
                break;

            case GameState.Eat:
                eatTimer = eatDuration;
                onEnterEat?.Invoke();
                break;

            case GameState.Return:
                onEnterReturn?.Invoke();
                break;
        }
    }
}