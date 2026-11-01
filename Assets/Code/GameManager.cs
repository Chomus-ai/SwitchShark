using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

public enum GameState { Intro, Rise, Surface }

/// <summary>
/// Drives the game loop: Intro -> Rise -> Surface -> Intro.
///
/// The manager is passive: it reads values and decides WHEN to change state.
///   Rise    : shark height >= maxHeight - arriveTolerance
///   Surface : short timer (the "you made it" moment), then back to Intro
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
    [Tooltip("Seconds spent on the arrival moment before returning to the Intro.")]
    [SerializeField] private float surfaceDuration = 8f;

    [Header("State enter events (hook up cameras, UI, controllers here)")]
    [SerializeField] private UnityEvent onEnterIntro;
    [SerializeField] private UnityEvent onEnterRise;
    [SerializeField] private UnityEvent onEnterSurface;

    [Header("Cutscenes (optional - leave empty to skip)")]
    [Tooltip("Plays when the player starts from the Intro. The game stays in Intro until it ends (or is skipped), then enters Rise.")]
    [SerializeField] private Cutscene openingCutscene;
    [Tooltip("Plays when the shark reaches the surface. The Surface timer only starts counting down once it has finished.")]
    [SerializeField] private Cutscene endingCutscene;

    [Header("Development")]
    [Tooltip("Keyboard fallback for starting the game from the Intro screen.")]
    [SerializeField] private Key debugStartKey = Key.Space;

    /// <summary>Fired after every state change: (previous, new).</summary>
    public event Action<GameState, GameState> StateChanged;

    public GameState CurrentState { get; private set; } = GameState.Intro;

    /// <summary>Shark height from 0 (seabed) to 1 (target height). Use this for the depth meter.</summary>
    public float NormalizedHeight => Mathf.InverseLerp(minHeight, maxHeight, CurrentHeight);

    /// <summary>Seconds left in the Surface segment (only meaningful during Surface).</summary>
    public float SurfaceTimeRemaining => surfaceTimer;

    // If your shark controller keeps its own height value, read that here instead
    // so there is only ever one source of truth for the shark's height.
    private float CurrentHeight => shark.position.y;

    /// <summary>True while the opening or ending cutscene is playing.</summary>
    public bool IsCutscenePlaying => openingActive || (endingCutscene != null && endingCutscene.IsPlaying);

    private float surfaceTimer;
    private bool openingActive;

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
                if (CurrentHeight >= maxHeight - arriveTolerance)
                    ChangeState(GameState.Surface);
                break;

            case GameState.Surface:
                // Hold the Surface timer until the ending cutscene has finished.
                if (endingCutscene != null && endingCutscene.IsPlaying) break;

                surfaceTimer = Mathf.Max(0f, surfaceTimer - Time.deltaTime);
                if (surfaceTimer <= 0f)
                    ChangeState(GameState.Intro);
                break;
        }
    }

    // ---------- Public API (call from the input layer) ----------

    /// <summary>Called when the player presses the start button on the Intro screen.</summary>
    public void RequestStart()
    {
        if (CurrentState != GameState.Intro) return;

        // No opening cutscene assigned: start straight away, as before.
        if (openingCutscene == null)
        {
            ChangeState(GameState.Rise);
            return;
        }

        if (!openingActive)
        {
            // First squeeze: play the opening. We stay in Intro until it finishes.
            openingActive = true;
            openingCutscene.Play(OnOpeningFinished);
        }
        else
        {
            // Squeezed again while it plays: skip (ignored during the cutscene's skip lock-out).
            openingCutscene.Skip();
        }
    }

    private void OnOpeningFinished()
    {
        if (!openingActive) return;
        openingActive = false;

        if (CurrentState == GameState.Intro)
            ChangeState(GameState.Rise);
    }

    /// <summary>Force back to Intro (e.g. from an inactivity timeout).</summary>
    public void ResetToIntro()
    {
        if (CurrentState != GameState.Intro)
        {
            ChangeState(GameState.Intro);
        }
        else if (openingActive)
        {
            // Abort an opening cutscene that is still playing.
            openingActive = false;
            openingCutscene.Stop();
        }
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
                // Leaving the Surface (or a reset): make sure no cutscene is left running.
                openingActive = false;
                if (openingCutscene != null) openingCutscene.Stop();
                if (endingCutscene != null) endingCutscene.Stop();
                onEnterIntro?.Invoke();
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