using UnityEngine;

/// <summary>
/// Optional. Makes a fish swim slowly forward while weaving left and right.
/// Add it to each fish prefab. "Forward" is the prefab's blue Z axis.
/// </summary>
public class SimpleFishSwim : MonoBehaviour
{
    [SerializeField] private float speed = 1.5f;
    [Tooltip("Peak turning speed of the weaving, in degrees per second.")]
    [SerializeField] private float weaveTurnRate = 25f;
    [SerializeField] private float weaveFrequency = 0.5f;

    private float phase;

    private void Start()
    {
        phase = Random.value * 10f; // so fish don't all weave in sync
    }

    private void Update()
    {
        float turn = Mathf.Sin((Time.time + phase) * weaveFrequency * Mathf.PI * 2f) * weaveTurnRate;
        transform.Rotate(0f, turn * Time.deltaTime, 0f, Space.World);
        transform.position += transform.forward * speed * Time.deltaTime;
    }
}
