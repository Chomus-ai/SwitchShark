using UnityEngine;

/// <summary>
/// Slow, aimless jellyfish drift: bobs with each bell pulse, wanders around its
/// starting point and turns gently. Jellyfish are not edible and have no collider.
/// </summary>
public class JellyDrift : MonoBehaviour
{
    [SerializeField] private float wanderRadius = 4f;
    [SerializeField] private float driftSpeed = 0.12f;
    [SerializeField] private float bobAmplitude = 0.25f;
    [SerializeField] private float pulsePeriod = 3f;
    [SerializeField] private float turnSpeed = 6f;

    private Vector3 home;
    private float seed;

    private void Start()
    {
        home = transform.position;
        seed = Random.value * 100f;
    }

    private void Update()
    {
        float t = Time.time * driftSpeed / Mathf.Max(0.01f, wanderRadius) + seed;
        Vector3 offset = new Vector3(Mathf.PerlinNoise(t, seed) - 0.5f, 0f, Mathf.PerlinNoise(seed, t) - 0.5f) * (2f * wanderRadius);

        // Each pulse lifts the bell a little, then it sinks back.
        float phase = (Time.time + seed) / pulsePeriod % 1f;
        float bob = (phase < 0.3f ? phase / 0.3f : 1f - (phase - 0.3f) / 0.7f) * bobAmplitude;

        transform.position = home + offset + Vector3.up * bob;
        transform.Rotate(0f, turnSpeed * Time.deltaTime * Mathf.Sin(t), 0f, Space.World);
    }
}
