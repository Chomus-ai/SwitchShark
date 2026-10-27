using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

[System.Serializable]
public class FishSpecies
{
    public string name = "Species";
    public GameObject prefab;
    [Tooltip("Relative chance of being picked. 2 is twice as common as 1. 0 disables the species.")]
    [Min(0f)] public float spawnWeight = 1f;
    [Tooltip("Each fish gets a random scale multiplier between X and Y.")]
    public Vector2 scaleRange = new Vector2(1f, 1f);

    [Header("Height band (world Y)")]
    [Tooltip("This species spawns between X (min) and Y (max) world height.")]
    public Vector2 heightRange = new Vector2(0f, 1000f);
    [Tooltip("Outside the band, spawn weight tapers to zero over this distance. 0 = hard cutoff.")]
    [Min(0f)] public float fadeMargin = 5f;

    /// <summary>Spawn weight at a given world height (0 if the species can't live there).</summary>
    public float WeightAt(float y)
    {
        if (prefab == null || spawnWeight <= 0f) return 0f;
        float outside = Mathf.Max(heightRange.x - y, y - heightRange.y, 0f);
        if (outside <= 0f) return spawnWeight;
        if (fadeMargin <= 0f) return 0f;
        return spawnWeight * Mathf.Clamp01(1f - outside / fadeMargin);
    }
}

/// <summary>
/// Spawns fish ahead of the shark while the game is in the Rise state.
/// Fish are biased to spawn above the shark (within its climbable cone) and
/// species are chosen based on the world height at the spawn point.
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

    [Header("Horizontal area (relative to the shark)")]
    [Tooltip("Min / max distance ahead of the shark.")]
    [SerializeField] private Vector2 distanceAhead = new Vector2(20f, 35f);
    [Tooltip("Fish spread this far to the left and right of the shark's path.")]
    [SerializeField] private float lateralRange = 12f;

    [Header("Vertical placement")]
    [Range(0f, 1f)]
    [Tooltip("Chance that a fish spawns in the band ABOVE the shark rather than level with it.")]
    [SerializeField] private float aboveBias = 0.7f;
    [Tooltip("Match SharkRise's max climb angle (degrees). Limits how high fish can be placed so they stay reachable.")]
    [SerializeField] private float maxClimbAngle = 20f;
    [Range(0.1f, 1f)]
    [Tooltip("Fraction of the reachable climb height that can be used. <1 keeps fish comfortably catchable.")]
    [SerializeField] private float reachFraction = 0.9f;
    [Tooltip("Minimum height above the shark for fish in the upper band.")]
    [SerializeField] private float minAbove = 1f;
    [Tooltip("Fish not in the upper band spread this far above/below the shark's height.")]
    [SerializeField] private float verticalJitter = 2f;
    [Tooltip("World height of the water surface (match GameManager maxHeight). Fish never spawn above it.")]
    [SerializeField] private float surfaceHeight = 100f;
    [SerializeField] private float surfaceMargin = 3f;

    [Header("Spacing & terrain")]
    [Tooltip("Spawn attempts are retried if a fish would appear closer than this to another.")]
    [SerializeField] private float minSpacing = 3f;
    [Tooltip("Minimum distance between a fish and the terrain floor so it never clips.")]
    [SerializeField] private float terrainBufferHeight = 1.5f;

    [Header("Cleanup")]
    [Tooltip("Fish this far behind the shark are removed.")]
    [SerializeField] private float despawnBehindDistance = 15f;
    [Tooltip("Fish this far away in any direction are removed.")]
    [SerializeField] private float despawnMaxDistance = 60f;

    private readonly List<GameObject> alive = new List<GameObject>();
    private bool spawning;
    private float timer;

    private readonly Dictionary<EntityId, ObjectPool<GameObject>> poolDictionary =
        new Dictionary<EntityId, ObjectPool<GameObject>>();
    private readonly Dictionary<GameObject, ObjectPool<GameObject>> fishToPoolMap =
        new Dictionary<GameObject, ObjectPool<GameObject>>();

    private void Awake()
    {
        if (gameManager == null) Debug.LogError("FishSpawner: 'gameManager' is not assigned.", this);
        if (shark == null) Debug.LogError("FishSpawner: 'shark' is not assigned.", this);

        if (fishParent == null)
            fishParent = new GameObject("Spawned Fish").transform;

        InitializePools();
    }

    private void OnValidate()
    {
        foreach (FishSpecies s in species)
        {
            if (s.heightRange.x > s.heightRange.y)
                s.heightRange = new Vector2(s.heightRange.y, s.heightRange.x);
        }
    }

    private void InitializePools()
    {
        foreach (FishSpecies s in species)
        {
            if (s.prefab == null)
            {
                Debug.LogWarning($"FishSpawner: species '{s.name}' has no prefab and will be skipped.", this);
                continue;
            }

            GameObject prefabReference = s.prefab;
            EntityId prefabID = prefabReference.GetEntityId();

            if (!poolDictionary.ContainsKey(prefabID))
            {
                var pool = new ObjectPool<GameObject>(
                    createFunc: () => Instantiate(prefabReference, fishParent),
                    actionOnGet: obj => obj.SetActive(true),
                    actionOnRelease: obj => obj.SetActive(false),
                    actionOnDestroy: obj => { if (obj != null) Destroy(obj); },
                    collectionCheck: false,
                    defaultCapacity: maxAlive,
                    maxSize: 50
                );
                poolDictionary.Add(prefabID, pool);
            }
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
        if (next == GameState.Rise)
            StartSpawning();
        else if (previous == GameState.Rise)
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
        PruneEatenFish();
        if (alive.Count >= maxAlive) return;

        // Position first, then pick a species that lives at that height.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Vector3 pos = RandomSpawnPosition();
            if (IsTooClose(pos)) continue;

            FishSpecies chosen = PickSpecies(pos.y);
            if (chosen == null) continue; // nothing lives at this height, try a new spot

            if (!poolDictionary.TryGetValue(chosen.prefab.GetEntityId(), out ObjectPool<GameObject> pool))
                continue;

            GameObject fish = GetFromPool(pool);
            if (fish == null) continue;

            Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            fish.transform.SetPositionAndRotation(pos, rot);

            // Reset to the prefab's base scale, then apply the random multiplier.
            fish.transform.localScale =
                chosen.prefab.transform.localScale * Random.Range(chosen.scaleRange.x, chosen.scaleRange.y);

            Edible edible = fish.GetComponent<Edible>();
            if (edible == null) edible = fish.AddComponent<Edible>();
            edible.SpeciesName = chosen.name;

            fishToPoolMap[fish] = pool;
            alive.Add(fish);
            return;
        }
    }

    /// <summary>Gets a live fish from the pool, skipping any that were destroyed externally.</summary>
    private GameObject GetFromPool(ObjectPool<GameObject> pool)
    {
        for (int i = 0; i < 3; i++)
        {
            GameObject fish = pool.Get();
            if (fish != null) return fish;
        }
        return null;
    }

    private FishSpecies PickSpecies(float y)
    {
        float total = 0f;
        foreach (FishSpecies s in species)
            total += s.WeightAt(y);

        if (total <= 0f) return null;

        float roll = Random.Range(0f, total);
        foreach (FishSpecies s in species)
        {
            float w = s.WeightAt(y);
            if (w <= 0f) continue;
            roll -= w;
            if (roll <= 0f) return s;
        }
        return null;
    }

    private Vector3 RandomSpawnPosition()
    {
        Vector3 forward = FlatForward();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        float distance = Random.Range(distanceAhead.x, distanceAhead.y);
        Vector3 pos = shark.position
                      + forward * distance
                      + right * Random.Range(-lateralRange, lateralRange);

        pos.y = shark.position.y + PickHeightOffset(distance);

        // Never above the surface.
        pos.y = Mathf.Min(pos.y, surfaceHeight - surfaceMargin);

        // Never inside the terrain.
        if (Terrain.activeTerrain != null)
        {
            float floorY = Terrain.activeTerrain.SampleHeight(pos) + Terrain.activeTerrain.transform.position.y;
            pos.y = Mathf.Max(pos.y, floorY + terrainBufferHeight);
        }

        return pos;
    }

    /// <summary>
    /// Mostly returns a height ABOVE the shark, capped by how far it can actually
    /// climb over the given horizontal distance. Otherwise a small level jitter.
    /// </summary>
    private float PickHeightOffset(float distance)
    {
        if (Random.value < aboveBias)
        {
            float reachable = distance * Mathf.Tan(maxClimbAngle * Mathf.Deg2Rad) * reachFraction;
            return Random.Range(minAbove, Mathf.Max(minAbove, reachable));
        }
        return Random.Range(-verticalJitter, verticalJitter);
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

    /// <summary>Drops destroyed fish and recycles fish that were deactivated (eaten).</summary>
    private void PruneEatenFish()
    {
        for (int i = alive.Count - 1; i >= 0; i--)
        {
            GameObject f = alive[i];
            if (f == null) { alive.RemoveAt(i); continue; }
            if (!f.activeInHierarchy) { DespawnFish(f); alive.RemoveAt(i); }
        }
    }

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
                DespawnFish(f);
                alive.RemoveAt(i);
            }
        }
    }

    private void ClearAll()
    {
        for (int i = alive.Count - 1; i >= 0; i--)
            if (alive[i] != null) DespawnFish(alive[i]);
        alive.Clear();

        // Forget map entries for fish that were destroyed externally.
        var dead = new List<GameObject>();
        foreach (var kv in fishToPoolMap)
            if (kv.Key == null) dead.Add(kv.Key);
        foreach (var k in dead) fishToPoolMap.Remove(k);
    }

    private void DespawnFish(GameObject fish)
    {
        if (fishToPoolMap.TryGetValue(fish, out ObjectPool<GameObject> sourcePool))
            sourcePool.Release(fish);
        else
            Destroy(fish);
    }

    private Vector3 FlatForward()
    {
        Vector3 f = shark.forward;
        f.y = 0f;
        return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    // Draws the upper spawn band in the Scene view when this object is selected.
    private void OnDrawGizmosSelected()
    {
        if (shark == null) return;
        Vector3 forward = FlatForward();
        float depth = Mathf.Max(0.1f, distanceAhead.y - distanceAhead.x);
        float topReach = distanceAhead.y * Mathf.Tan(maxClimbAngle * Mathf.Deg2Rad) * reachFraction;
        float height = Mathf.Max(0.1f, topReach - minAbove);

        Vector3 centre = shark.position
                         + forward * (distanceAhead.x + depth * 0.5f)
                         + Vector3.up * (minAbove + height * 0.5f);
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.matrix = Matrix4x4.TRS(centre, Quaternion.LookRotation(forward), Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(lateralRange * 2f, height, depth));
    }
}