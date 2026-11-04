using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

// Title is appended at the end so any serialized GameState values keep their meaning.
public enum GameState { Intro, Rise, Surface, Title }

/// <summary>
/// Drives the game loop: Title -> Intro -> Rise -> Surface -> Title.
///
/// The manager is passive: it reads values and decides WHEN to change state.
///   Title   : waits for the first squeeze (title card is shown here)
///   Intro   : opening cutscene plays; a second squeeze skips it
///   Rise    : shark height >= maxHeight - arriveTolerance
///   Surface : ending cutscene plays, then a short timer, then back to Title
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
    [Tooltip("Seabed height. Used for the depth meter (0 at this height).")]
    [SerializeField] private float minHeight = 0f;
    [Tooltip("Rise ends when the shark reaches this height.")]
    [SerializeField] private float maxHeight = 100f;
    [Tooltip("The shark approaches its target height smoothly, so count it as arrived when this close.")]
    [SerializeField] private float arriveTolerance = 0.25f;

    [Header("Surface segment")]
    [Tooltip("Seconds spent on the arrival moment before returning to the Title screen.")]
    [SerializeField] private float surfaceDuration = 8f;

    [Header("State enter events (hook up cameras, UI, controllers here)")]
    [Tooltip("Idle screen: show the title card, reset the shark pose, switch to the idle camera.")]
    [SerializeField] private UnityEvent onEnterTitle;
    [SerializeField] private UnityEvent onEnterIntro;
    [SerializeField] private UnityEvent onEnterRise;
    [SerializeField] private UnityEvent onEnterSurface;

    [Header("Cutscenes (optional - leave empty to skip)")]
    [Tooltip("Plays when the player squeezes on the Title screen. The game stays in Intro until it ends (or is skipped), then enters Rise.")]
    [SerializeField] private Cutscene openingCutscene;
    [Tooltip("Plays when the shark reaches the surface. The Surface timer only starts counting down once it has finished.")]
    [SerializeField] private Cutscene endingCutscene;

    [Header("Development")]
    [Tooltip("Keyboard fallback for starting the game from the Title screen.")]
    [SerializeField] private Key debugStartKey = Key.Space;

    /// <summary>Fired after every state change: (previous, new).</summary>
    public event Action<GameState, GameState> StateChanged;

    public GameState CurrentState { get; private set; } = GameState.Title;

    /// <summary>Shark height from 0 (seabed) to 1 (target height). Use this for the depth meter.</summary>
    public float NormalizedHeight => Mathf.InverseLerp(minHeight, maxHeight, CurrentHeight);

    /// <summary>Seconds left in the Surface segment (only meaningful during Surface).</summary>
    public float SurfaceTimeRemaining => surfaceTimer;

    // If your shark controller keeps its own height value, read that here instead
    // so there is only ever one source of truth for the shark's height.
    private float CurrentHeight => shark.position.y;

    /// <summary>True while the opening or ending cutscene is playing.</summary>
    public bool IsCutscenePlaying =>
        (openingCutscene != null && openingCutscene.IsPlaying) ||
        (endingCutscene != null && endingCutscene.IsPlaying);

    private float surfaceTimer;

    private void Awake()
    {
        if (shark == null)
            Debug.LogError("GameManager: 'shark' is not assigned.", this);
    }

    private void Start()
    {
        // First entry into Title: there is no previous state to exit.
        EnterState(GameState.Title);
    }

    private void Update()
    {
        if (shark == null) return;

        switch (CurrentState)
        {
            case GameState.Title:
            case GameState.Intro:
                Keyboard keyboard = Keyboard.current; // null if no keyboard is connected
                if (keyboard != null && keyboard[debugStartKey].wasPressedThisFrame)
                    RequestStart();
                break;

            case GameState.Rise:
                if (CurrentHeight >= maxHeight - arriveTolerance)
                    ChangeState(GameState.Surface);
                break;

            case GameState.Surface:
                // Hold the Surface timer until the ending cutscene has finished.
                if (endingCutscene != null && endingCutscene.IsPlaying) break;

                surfaceTimer = Mathf.Max(0f, surfaceTimer - Time.deltaTime);
                if (surfaceTimer <= 0f)
                    ChangeState(GameState.Title);
                break;
        }
    }

    // ---------- Public API (call from the input layer) ----------

    /// <summary>Called when the player squeezes / presses the start button.</summary>
    public void RequestStart()
    {
        switch (CurrentState)
        {
            case GameState.Title:
                // First squeeze. With no opening cutscene assigned, go straight to Rise.
                ChangeState(openingCutscene == null ? GameState.Rise : GameState.Intro);
                break;

            case GameState.Intro:
                // Squeezed again while the opening plays: skip it
                // (ignored during the cutscene's skip lock-out).
                if (openingCutscene != null) openingCutscene.Skip();
                break;
        }
    }

    private void OnOpeningFinished()
    {
        if (CurrentState == GameState.Intro)
            ChangeState(GameState.Rise);
    }

    /// <summary>Force back to the Title screen (e.g. from an inactivity timeout).</summary>
    public void ResetToTitle()
    {
        // EnterState(Title) stops any running cutscene.
        if (CurrentState != GameState.Title)
            ChangeState(GameState.Title);
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
            case GameState.Title:
                // Returning from Surface (or a reset): make sure no cutscene is left running.
                if (openingCutscene != null) openingCutscene.Stop();
                if (endingCutscene != null) endingCutscene.Stop();
                onEnterTitle?.Invoke();
                break;

            case GameState.Intro:
                onEnterIntro?.Invoke();
                if (openingCutscene != null) openingCutscene.Play(OnOpeningFinished);
                break;

            case GameState.Rise:
                onEnterRise?.Invoke();
                break;

            case GameState.Surface:
                surfaceTimer = surfaceDuration;
                onEnterSurface?.Invoke();
                if (endingCutscene != null) endingCutscene.Play();
                break;
        }
    }
}