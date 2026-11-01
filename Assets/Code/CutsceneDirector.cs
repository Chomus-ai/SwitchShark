using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;

/// <summary>
/// Timeline version of Cutscene: wraps a PlayableDirector so GameManager can start it, be told
/// when it ends, and let the player skip it. Put it on the same GameObject as the PlayableDirector.
/// (If you don't want to author a Timeline, use ShotCutscene instead.)
///
/// Fail-safes (an exhibit must never get stuck on a cutscene):
///   - no Timeline asset assigned -> the completion callback fires immediately
///   - Wrap Mode is forced to None -> the director really stops at the end and tells us
/// Keep this GameObject active in the scene, otherwise Awake never runs and Play() can't start.
/// </summary>
[RequireComponent(typeof(PlayableDirector))]
public class CutsceneDirector : Cutscene
{
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

    private PlayableDirector director;
    private Action finishedCallback;
    private float startTime;
    private bool playing;

    public override bool IsPlaying => playing;
    public override bool CanSkip => playing && allowSkip && Time.unscaledTime - startTime >= skipLockout;

    private void Awake()
    {
        director = GetComponent<PlayableDirector>();
        director.playOnAwake = false;
        // With Wrap Mode = Hold/Loop the director never stops, so 'stopped' would never fire.
        director.extrapolationMode = DirectorWrapMode.None;
        director.stopped += OnDirectorStopped;
    }

    private void Start()
    {
        if (playOnStart) Play();
    }

    private void OnDestroy()
    {
        if (director != null) director.stopped -= OnDirectorStopped;
    }

    public override void Play(Action onComplete = null)
    {
        if (playing) return;

        if (director.playableAsset == null)
        {
            Debug.LogWarning($"[CutsceneDirector] '{name}' has no Timeline asset assigned - skipping straight to the end.", this);
            onStarted?.Invoke();
            onFinished?.Invoke();
            onComplete?.Invoke();
            return;
        }

        finishedCallback = onComplete;
        playing = true;
        startTime = Time.unscaledTime;

        onStarted?.Invoke();
        director.time = 0;
        director.Play();
    }

    public override void Skip()
    {
        if (!CanSkip) return;

        director.time = director.duration;
        director.Evaluate();
        director.Stop(); // raises 'stopped' -> OnDirectorStopped -> completion callback
    }

    public override void Stop()
    {
        if (!playing) return;

        finishedCallback = null;
        director.Stop();
    }

    private void OnDirectorStopped(PlayableDirector stopped)
    {
        if (!playing) return;
        playing = false;

        Action callback = finishedCallback;
        finishedCallback = null;

        onFinished?.Invoke();
        callback?.Invoke();
    }
}
