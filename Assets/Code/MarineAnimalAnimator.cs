using UnityEngine;

/// In-place animation API shared by the footage-based prawn and eel-like fish.
[RequireComponent(typeof(Animator))]
public class MarineAnimalAnimator : MonoBehaviour
{
    public string[] availableClips = System.Array.Empty<string>();
    Animator animator;

    void Awake()
    {
        animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
    }

    /// Returns false if this animal has no matching clip. Movement belongs to game code.
    public bool Play(string action, float fadeSeconds = 0.18f)
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (!isActiveAndEnabled || animator.runtimeAnimatorController == null ||
            availableClips == null || System.Array.IndexOf(availableClips, action) < 0) return false;
        animator.CrossFadeInFixedTime(Animator.StringToHash(action), Mathf.Max(0f, fadeSeconds));
        return true;
    }

    [ContextMenu("Play Swim")] public void Swim() => Play("Swim");
    [ContextMenu("Play Idle / Glide")] public void Rest() => Play(System.Array.IndexOf(availableClips, "Idle") >= 0 ? "Idle" : "Glide");
    [ContextMenu("Play Feeding")] public void Feed() => Play("Feed");
    [ContextMenu("Play Turn Left")] public void TurnLeft() => Play("TurnLeft");
    [ContextMenu("Play Turn Right")] public void TurnRight() => Play("TurnRight");
    [ContextMenu("Play Tail Flip")] public void TailFlip() => Play("TailFlip");
}
