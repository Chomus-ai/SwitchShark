using UnityEngine;

/// <summary>
/// Drives the shark model's looping "Swim" animation from the shark's actual speed:
/// slow tail beats while resting on the seabed, a steady cruising beat while rising,
/// and faster beats during a boost.
///
/// Attach to the shark model object (the one with the Animator), or any object, and
/// assign the references. The Animator should have a single looping Swim state.
/// </summary>
public class SharkSwimAnimator : MonoBehaviour
{
    [SerializeField] private SharkRise shark;
    [SerializeField] private Animator animator;

    [Header("Animation speed (1 = the clip's own speed, 0.75 tail beats per second)")]
    [Tooltip("While the shark is resting (Intro).")]
    [SerializeField] private float restingAnimSpeed = 0.4f;
    [Tooltip("Animation speed when the shark moves at 'Reference Speed'.")]
    [SerializeField] private float cruiseAnimSpeed = 1.2f;
    [Tooltip("The swimming speed (game units per second) that should give 'Cruise Anim Speed'. " +
             "Use SharkRise's Forward Speed.")]
    [SerializeField] private float referenceSpeed = 5f;
    [SerializeField] private float maxAnimSpeed = 2.5f;
    [Tooltip("Seconds of smoothing so the tail doesn't jerk when the speed changes.")]
    [SerializeField] private float smoothing = 0.2f;

    private float current;

    private void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (shark == null) shark = GetComponentInParent<SharkRise>();

        if (animator == null)
            Debug.LogError("SharkSwimAnimator: no Animator found. Assign one.", this);
        if (shark == null)
            Debug.LogError("SharkSwimAnimator: no SharkRise found. Assign one.", this);

        current = restingAnimSpeed;
    }

    private void Update()
    {
        if (animator == null || shark == null) return;

        float speed = shark.CurrentSpeed;
        float target = speed <= 0.01f
            ? restingAnimSpeed
            : Mathf.Min(maxAnimSpeed, cruiseAnimSpeed * speed / Mathf.Max(0.01f, referenceSpeed));

        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, smoothing));
        current = Mathf.Lerp(current, target, k);

        animator.speed = current;
    }
}
