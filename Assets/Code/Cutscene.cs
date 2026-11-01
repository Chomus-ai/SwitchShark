using System;
using UnityEngine;

/// <summary>
/// Common face of every cutscene type, so GameManager doesn't care how a cutscene is made.
///   ShotCutscene     : camera shots + fading cards set up in the Inspector (no Timeline needed)
///   CutsceneDirector : plays a Timeline asset
/// </summary>
public abstract class Cutscene : MonoBehaviour
{
    /// <summary>True from Play() until the cutscene ends, is skipped or is stopped.</summary>
    public abstract bool IsPlaying { get; }

    /// <summary>True when Skip() would currently do something (playing, skipping allowed, lock-out passed).</summary>
    public abstract bool CanSkip { get; }

    /// <summary>Starts the cutscene. onComplete is called when it ends or is skipped (not when stopped).</summary>
    public abstract void Play(Action onComplete = null);

    /// <summary>Finishes early, if skipping is allowed right now.</summary>
    public abstract void Skip();

    /// <summary>Aborts without calling the completion callback.</summary>
    public abstract void Stop();
}
