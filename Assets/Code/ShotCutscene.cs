using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A cutscene made of camera shots and fading cards, set up entirely in the Inspector:
///   - Shots: the cutscene camera glides from one marker Transform to another (or stays put),
///            optionally always looking at a target (e.g. the shark).
///   - Cards: CanvasGroups (title card, end card...) that fade in, hold, fade out at set times.
///
/// Camera: 'Camera Object' is a CinemachineCamera GameObject that is INACTIVE in the scene.
/// It is switched on when the cutscene starts (so Cinemachine makes it the live camera, the same
/// way GameManager's events switch 2DSide / 3DBack) and switched off BEFORE the completion
/// callback, so the next state's camera takes over.
///
/// Tip: parent the marker Transforms to the shark so the shots follow it (the shark keeps
/// gliding during the Surface state).
///
/// Fail-safe: with no camera or no shots assigned, the cutscene completes immediately instead
/// of hanging the game.
/// Testable on its own: tick 'Play On Start', press Play.
/// </summary>
[DefaultExecutionOrder(-50)] // LateUpdate runs before Cinemachine's brain reads the camera pose
public class ShotCutscene : Cutscene
{
    [Serializable]
    public class Shot
    {
        public string name = "Shot";
        [Tooltip("Where the camera starts. Parent this marker to the shark so the shot follows it.")]
        public Transform from;
        [Tooltip("Where the camera ends. Leave empty for a locked-off shot at 'From'.")]
        public Transform to;
        [Min(0.1f)] public float duration = 4f;
        [Tooltip("Speed curve of the move: 0 = at 'From', 1 = at 'To'.")]
        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("If set, the camera always looks at this instead of using the markers' rotations.")]
        public Transform lookAt;
    }

    [Serializable]
    public class Card
    {
        public string name = "Card";
        public CanvasGroup group;
        [Tooltip("Seconds after the cutscene starts that the card begins to fade in.")]
        public float startTime = 1f;
        public float fadeIn = 1f;
        public float hold = 3f;
        public float fadeOut = 1f;

        public float EndTime => startTime + fadeIn + hold + fadeOut;
    }

    [Header("Camera")]
    [Tooltip("A CinemachineCamera object that is inactive in the scene. It is moved along the shots below.")]
    [SerializeField] private GameObject cameraObject;

    [Header("Content")]
    [SerializeField] private List<Shot> shots = new List<Shot>();
    [SerializeField] private List<Card> cards = new List<Card>();
    [Tooltip("Extra seconds to linger after the last shot / card ends.")]
    [SerializeField] private float tailPause = 0.5f;

    [Header("Audio (optional)")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip clip;

    [Header("Skipping")]
    [SerializeField] private bool allowSkip = true;
    [Tooltip("Seconds after the cutscene starts during which skip presses are ignored (stops a double squeeze skipping it).")]
    [SerializeField] private float skipLockout = 1.5f;

    [Header("Hooks (optional)")]
    [Tooltip("Fires when the cutscene starts, e.g. hide the HUD.")]
    [SerializeField] private UnityEvent onStarted;
    [Tooltip("Fires when it ends, is skipped or is aborted, e.g. show the HUD again.")]
    [SerializeField] private UnityEvent onFinished;

    [Header("Testing")]
    [SerializeField] private bool playOnStart = false;

    private bool playing;
    private float elapsed;
    private float totalDuration;
    private float startTime;
    private Action finishedCallback;

    public override bool IsPlaying => playing;
    public override bool CanSkip => playing && allowSkip && Time.unscaledTime - startTime >= skipLockout;

    /// <summary>Used by the editor setup tool (SwitchShark > Create Cutscene Setup) to fill in the content.</summary>
    public void Configure(GameObject camera, List<Shot> newShots, List<Card> newCards, float tail)
    {
        cameraObject = camera;
        shots = newShots;
        cards = newCards;
        tailPause = tail;
    }

    private void Start()
    {
        HideCards(); // cards start invisible
        if (playOnStart) Play();
    }

    private void OnDisable()
    {
        if (playing) Finish(false);
    }

    public override void Play(Action onComplete = null)
    {
        if (playing) return;

        if (cameraObject == null || shots == null || shots.Count == 0)
        {
            Debug.LogWarning($"[ShotCutscene] '{name}' needs a camera object and at least one shot - skipping straight to the end.", this);
            onStarted?.Invoke();
            onFinished?.Invoke();
            onComplete?.Invoke();
            return;
        }

        finishedCallback = onComplete;
        playing = true;
        elapsed = 0f;
        startTime = Time.unscaledTime;
        totalDuration = ComputeTotalDuration();

        HideCards();

        // Place the camera on the first frame BEFORE activating it, so Cinemachine never shows a stale pose.
        Apply(0f);
        cameraObject.SetActive(true);

        if (audioSource != null && clip != null)
        {
            audioSource.clip = clip;
            audioSource.Play();
        }

        onStarted?.Invoke();
    }

    public override void Skip()
    {
        if (!CanSkip) return;
        Finish(true);
    }

    public override void Stop()
    {
        Finish(false);
    }

    private void LateUpdate()
    {
        if (!playing) return;

        elapsed += Time.deltaTime;
        Apply(elapsed);

        if (elapsed >= totalDuration)
            Finish(true);
    }

    private float ComputeTotalDuration()
    {
        float shotsTotal = 0f;
        foreach (Shot s in shots) shotsTotal += Mathf.Max(0.1f, s.duration);

        float cardsEnd = 0f;
        foreach (Card c in cards) cardsEnd = Mathf.Max(cardsEnd, c.EndTime);

        return Mathf.Max(shotsTotal, cardsEnd) + tailPause;
    }

    private void Apply(float t)
    {
        // --- Camera: find the shot that covers time t (the last shot holds its final frame). ---
        float start = 0f;
        for (int i = 0; i < shots.Count; i++)
        {
            Shot s = shots[i];
            float duration = Mathf.Max(0.1f, s.duration);
            bool isLast = i == shots.Count - 1;

            if (t < start + duration || isLast)
            {
                float local = Mathf.Clamp01((t - start) / duration);
                PoseCamera(s, s.ease != null && s.ease.length > 0 ? s.ease.Evaluate(local) : local);
                break;
            }
            start += duration;
        }

        // --- Cards ---
        foreach (Card c in cards)
        {
            if (c.group == null) continue;
            c.group.alpha = CardAlpha(c, t);
        }
    }

    private void PoseCamera(Shot s, float k)
    {
        if (s.from == null || cameraObject == null) return;

        Vector3 position = s.to != null ? Vector3.Lerp(s.from.position, s.to.position, k) : s.from.position;
        Quaternion rotation = s.to != null ? Quaternion.Slerp(s.from.rotation, s.to.rotation, k) : s.from.rotation;

        if (s.lookAt != null)
        {
            Vector3 direction = s.lookAt.position - position;
            if (direction.sqrMagnitude > 1e-6f)
                rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        cameraObject.transform.SetPositionAndRotation(position, rotation);
    }

    private static float CardAlpha(Card c, float t)
    {
        float local = t - c.startTime;
        if (local <= 0f) return 0f;
        if (local < c.fadeIn) return local / c.fadeIn;
        if (local < c.fadeIn + c.hold) return 1f;

        float fadingOut = local - c.fadeIn - c.hold;
        if (fadingOut < c.fadeOut) return 1f - fadingOut / c.fadeOut;
        return 0f;
    }

    private void HideCards()
    {
        if (cards == null) return;
        foreach (Card c in cards)
        {
            if (c.group == null) continue;
            c.group.alpha = 0f;
            c.group.interactable = false;
            c.group.blocksRaycasts = false;
        }
    }

    private void Finish(bool invokeCallback)
    {
        if (!playing) return;
        playing = false;

        HideCards();

        if (audioSource != null && clip != null && audioSource.clip == clip)
            audioSource.Stop();

        // Switch the cutscene camera off BEFORE the callback, so whichever camera the next state
        // enables (e.g. 3DBack when Rise begins) becomes the live one.
        if (cameraObject != null)
            cameraObject.SetActive(false);

        Action callback = invokeCallback ? finishedCallback : null;
        finishedCallback = null;

        onFinished?.Invoke();
        callback?.Invoke();
    }
}
