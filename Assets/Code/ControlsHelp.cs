using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// On-screen instructions:
///   Start prompt (bottom centre) : how to start, shown on the Intro screen; a short message at the surface
///   Controls card (bottom left)  : shown on the Intro and for the first seconds of the Rise, then fades out.
///                                   H shows or hides it at any time.
/// The text matches the active controls: the temporary mouse + keyboard controls, or the Joy-Con / SharkRise setup.
/// Built by SwitchShark > Build Underwater Scene.
/// </summary>
public class ControlsHelp : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [Tooltip("Optional. When it is active, mouse + keyboard instructions are shown.")]
    [SerializeField] private TempMouseSwimControls mouseControls;
    [Tooltip("Optional. Shows how many fish were eaten when the surface is reached. Found automatically if empty.")]
    [SerializeField] private SharkEater eater;

    [SerializeField] private CanvasGroup startPrompt;
    [SerializeField] private TMP_Text startTitle;
    [SerializeField] private TMP_Text startHint;
    [SerializeField] private CanvasGroup controlsCard;
    [SerializeField] private TMP_Text controlsBody;
    [SerializeField] private CanvasGroup helpHint;

    [Tooltip("Seconds the controls card stays up after the game starts.")]
    [SerializeField] private float showAfterStart = 8f;
    [SerializeField] private float fadeSpeed = 3f;

    private const string MouseKeys =
        "<b>MOUSE</b><pos=34%>Look and steer\n" +
        "<b>W / S</b><pos=34%>Swim forward / back\n" +
        "<b>A / D</b><pos=34%>Slide left / right\n" +
        "<b>SHIFT</b><pos=34%>Swim faster\n" +
        "<b>SCROLL</b><pos=34%>Zoom camera\n" +
        "<b>ESC</b><pos=34%>Free the mouse\n" +
        "<b>H</b><pos=34%>Hide / show help\n" +
        "<b>F2</b><pos=34%>Use the Joy-Con\n\n" +
        "<i>Look up and hold W to rise toward the light.</i>";

    private const string ControllerKeys =
        "<b>TILT L / R</b><pos=40%>Turn\n" +
        "<b>TILT UP / DOWN</b><pos=40%>Climb / dive\n" +
        "<b>BUTTON</b><pos=40%>Speed burst\n\n" +
        "<b>Keyboard</b>\n" +
        "<b>A / D</b><pos=40%>Turn\n" +
        "<b>W / S</b><pos=40%>Climb / dive\n" +
        "<b>SPACE</b><pos=40%>Speed burst\n" +
        "<b>H</b><pos=40%>Hide / show help\n" +
        "<b>F2</b><pos=40%>Mouse + keyboard";

    private float riseStartTime = float.NegativeInfinity;
    private float surfaceMessageUntil = float.NegativeInfinity;
    [Tooltip("Seconds the 'You reached the surface!' message stays up.")]
    [SerializeField] private float surfaceMessageTime = 5f;
    private bool? pinned; // null = automatic, true/false = player pressed H

    private bool MouseMode => mouseControls != null && mouseControls.IsActive;

    private void Start()
    {
        if (eater == null) eater = FindAnyObjectByType<SharkEater>();
    }

    private void OnEnable()
    {
        if (gameManager != null)
        {
            gameManager.StateChanged += HandleStateChanged;
            gameManager.SurfaceReached += HandleSurfaceReached;
        }
    }

    private void OnDisable()
    {
        if (gameManager != null)
        {
            gameManager.StateChanged -= HandleStateChanged;
            gameManager.SurfaceReached -= HandleSurfaceReached;
        }
    }

    private void HandleSurfaceReached() => surfaceMessageUntil = Time.time + surfaceMessageTime;

    private void HandleStateChanged(GameState previous, GameState next)
    {
        if (next == GameState.Rise) riseStartTime = Time.time;
        if (next == GameState.Intro) pinned = null;
    }

    private void Update()
    {
        if (gameManager == null) return;
        GameState state = gameManager.CurrentState;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.hKey.wasPressedThisFrame)
            pinned = !(controlsCard != null && controlsCard.alpha > 0.5f);

        // Start prompt; also the "reached the surface" message (as its own state, or for a few seconds mid-swim)
        bool surfaced = state == GameState.Surface || (state == GameState.Rise && Time.time < surfaceMessageUntil);
        bool showPrompt = state != GameState.Rise || surfaced;
        if (startTitle != null)
            startTitle.text = surfaced ? "You reached the surface!" : "Rise from the midnight zone";
        if (startHint != null)
            startHint.text = surfaced ? SurfaceSummary()
                           : MouseMode ? "Press  <b>SPACE</b>,  <b>W</b>  or  <b>CLICK</b>  to start"
                           : "Press the  <b>Joy-Con button</b>  or  <b>SPACE</b>  to start";
        Fade(startPrompt, showPrompt ? (state == GameState.Intro ? 0.75f + 0.25f * Mathf.Sin(Time.time * 3f) : 1f) : 0f);

        // Controls card
        if (controlsBody != null) controlsBody.text = MouseMode ? MouseKeys : ControllerKeys;
        bool automatic = state == GameState.Intro || Time.time - riseStartTime < showAfterStart;
        bool showCard = state != GameState.Surface && (pinned ?? automatic);
        Fade(controlsCard, showCard ? 1f : 0f);
        Fade(helpHint, showCard || state == GameState.Surface ? 0f : 0.8f);
    }

    private string SurfaceSummary()
    {
        int eaten = eater != null ? eater.EatenCount : 0;
        string fish = eaten == 1 ? "1 fish" : $"{eaten} fish";
        return gameManager.CurrentState == GameState.Surface
            ? $"You ate <b>{fish}</b> on the way up   ·   back to the deep in {Mathf.CeilToInt(gameManager.SurfaceTimeRemaining)}"
            : $"You ate <b>{fish}</b> on the way up   ·   keep exploring, or dive back down";
    }

    private void Fade(CanvasGroup group, float target)
    {
        if (group == null) return;
        group.alpha = Mathf.MoveTowards(group.alpha, target, fadeSpeed * Time.unscaledDeltaTime);
    }
}
