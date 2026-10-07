using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class FishSpecies
{
    public string name = "Species";
    public GameObject prefab;
    [Tooltip("Relative chance of being picked. 2 is twice as common as 1. 0 disables the species.")]
    [Min(0f)] public float spawnWeight = 1f;
    [Tooltip("Each fish gets a random scale multiplier between X and Y.")]
    public Vector2 scaleRange = new Vector2(1f, 1f);
}

/// <summary>
/// Spawns fish ahead of the shark while the game is in the Eat state.
/// When Eat begins: an initial burst of fish appears, then more spawn on a timer.
/// When Eat ends: every remaining fish is removed.
/// Fish are placed on the shark's depth plane (small vertical jitter), spread
/// sideways, so that left/right steering alone can reach them.
/// </summary>
public class FishSpawner : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameManager gameManager;
    [SerializeField] private Transform shark;
    [Tooltip("Optional. Spawned fish are parented here to keep the Hierarchy tidy.")]
    [SerializeField] private Transform fishParent;

    [Header("Species")]
    [SerializeField] private List<FishSpecies> species = new List<FishSpecies>();

    [Header("Spawn rate")]
    [SerializeField] private int initialBurst = 6;
    [SerializeField] private float spawnInterval = 1.5f;
    [SerializeField] private int maxAlive = 15;

    [Header("Spawn area (relative to the shark, in its facing direction)")]
    [Tooltip("Min / max distance ahead of the shark.")]
    [SerializeField] private Vector2 distanceAhead = new Vector2(20f, 35f);
    [Tooltip("Fish spread this far to the left and right of the shark's path.")]
    [SerializeField] private float lateralRange = 12f;
    [Tooltip("Fish spread this far above and below the shark's height.")]
    [SerializeField] private float verticalJitter = 1f;
    [Tooltip("Spawn attempts are retried if a fish would appear closer than this to another.")]
    [SerializeField] private float minSpacing = 3f;

    [Header("Cleanup")]
    [Tooltip("Fish this far behind the shark are removed.")]
    [SerializeField] private float despawnBehindDistance = 15f;
    [Tooltip("Fish this far away in any direction are removed.")]
    [SerializeField] private float despawnMaxDistance = 60f;

    private readonly List<GameObject> alive = new List<GameObject>();
    private bool spawning;
    private float timer;

    private void Awake()
    {
        if (gameManager == null) Debug.LogError("FishSpawner: 'gameManager' is not assigned.", this);
        if (shark == null) Debug.LogError("FishSpawner: 'shark' is not assigned.", this);

        foreach (FishSpecies s in species)
            if (s.prefab == null)
                Debug.LogWarning($"FishSpawner: species '{s.name}' has no prefab and will be skipped.", this);

        if (fishParent == null)
        {
            fishParent = new GameObject("Spawned Fish").transform;
        }
    }

    private void OnEnable()
    {
        if (gameManager != null) gameManager.StateChanged += HandleStateChanged;
    }

    private void OnDisable()
    {
        if (gameManager != null) gameManager.StateChanged -= HandleStateChanged;
    }

    private void HandleStateChanged(GameState previous, GameState next)
    {
        if (next == GameState.Eat)
            StartSpawning();
        else if (previous == GameState.Eat)
            StopAndClear();
    }

    private void StartSpawning()
    {
        ClearAll();
        spawning = true;
        timer = spawnInterval;

        for (int i = 0; i < initialBurst; i++)
            TrySpawnOne();
    }

    private void StopAndClear()
    {
        spawning = false;
        ClearAll();
    }

    private void Update()
    {
        if (!spawning || shark == null) return;

        RemoveFarFish();

        timer -= Time.deltaTime;
        if (timer <= 0f)
        {
            timer = spawnInterval;
            TrySpawnOne();
        }
    }

    // ---------- Spawning ----------

    private void TrySpawnOne()
    {
        alive.RemoveAll(f => f == null); // fish that were eaten/destroyed elsewhere
        if (alive.Count >= maxAlive) return;

        FishSpecies chosen = PickSpecies();
        if (chosen == null) return;

        // A few attempts to find a spot that isn't on top of another fish.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Vector3 pos = RandomSpawnPosition();
            if (IsTooClose(pos)) continue;

            Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            GameObject fish = Instantiate(chosen.prefab, pos, rot, fishParent);

            float scale = Random.Range(chosen.scaleRange.x, chosen.scaleRange.y);
            fish.transform.localScale *= scale;

            // Make sure the fish can be eaten, and remember which species it is.
            Edible edible = fish.GetComponent<Edible>();
            if (edible == null) edible = fish.AddComponent<Edible>();
            edible.SpeciesName = chosen.name;

            alive.Add(fish);
            return;
        }
    }

    private FishSpecies PickSpecies()
    {
        float total = 0f;
        foreach (FishSpecies s in species)
            if (s.prefab != null) total += s.spawnWeight;

        if (total <= 0f) return null;

        float roll = Random.Range(0f, total);
        foreach (FishSpecies s in species)
        {
            if (s.prefab == null) continue;
            roll -= s.spawnWeight;
            if (roll <= 0f) return s;
        }
        return null;
    }

    private Vector3 RandomSpawnPosition()
    {
        Vector3 forward = FlatForward();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        Vector3 pos = shark.position
                      + forward * Random.Range(distanceAhead.x, distanceAhead.y)
                      + right * Random.Range(-lateralRange, lateralRange);
        pos.y = shark.position.y + Random.Range(-verticalJitter, verticalJitter);
        return pos;
    }

    private bool IsTooClose(Vector3 pos)
    {
        float sqr = minSpacing * minSpacing;
        foreach (GameObject f in alive)
            if (f != null && (f.transform.position - pos).sqrMagnitude < sqr)
                return true;
        return false;
    }

    // ---------- Cleanup ----------

    private void RemoveFarFish()
    {
        Vector3 forward = FlatForward();

        for (int i = alive.Count - 1; i >= 0; i--)
        {
            GameObject f = alive[i];
            if (f == null) { alive.RemoveAt(i); continue; }

            Vector3 toFish = f.transform.position - shark.position;
            bool tooFarBehind = Vector3.Dot(toFish, forward) < -despawnBehindDistance;
            bool tooFarAway = toFish.sqrMagnitude > despawnMaxDistance * despawnMaxDistance;

            if (tooFarBehind || tooFarAway)
            {
                Destroy(f);
                alive.RemoveAt(i);
            }
        }
    }

    private void ClearAll()
    {
        foreach (GameObject f in alive)
            if (f != null) Destroy(f);
        alive.Clear();
    }

    private Vector3 FlatForward()
    {
        Vector3 f = shark.forward;
        f.y = 0f;
        return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    // Draws the spawn area in the Scene view when this object is selected.
    private void OnDrawGizmosSelected()
    {
        if (shark == null) return;

        Vector3 forward = FlatForward();
        float depth = Mathf.Max(0.1f, distanceAhead.y - distanceAhead.x);
        Vector3 centre = shark.position + forward * (distanceAhead.x + depth * 0.5f);

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.matrix = Matrix4x4.TRS(centre, Quaternion.LookRotation(forward), Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(lateralRange * 2f, verticalJitter * 2f + 0.1f, depth));
    }
}