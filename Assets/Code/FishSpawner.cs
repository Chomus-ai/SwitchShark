using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool; // Required for optimized Object Pooling

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
/// Spawns fish ahead of the shark while the game is in the Rise state.
/// Object pooling is handled via individual pools grouped by prefab type.
/// Fish float freely but are checked against the active terrain height to ensure they don't clip into geometry.
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
    [SerializeField] private float verticalJitter = 2f;
    [Tooltip("Fish are placed this far ABOVE the shark's current height, because the shark is climbing and will be higher by the time it reaches them.")]
    [SerializeField] private float verticalLead = 3f;
    [Tooltip("Spawn attempts are retried if a fish would appear closer than this to another.")]
    [SerializeField] private float minSpacing = 3f;
    [Tooltip("Minimum spacing distance allowed between a fish and the terrain floor so it never clips.")]
    [SerializeField] private float terrainBufferHeight = 1.5f;

    [Header("Cleanup")]
    [Tooltip("Fish this far behind the shark are removed.")]
    [SerializeField] private float despawnBehindDistance = 15f;
    [Tooltip("Fish this far away in any direction are removed.")]
    [SerializeField] private float despawnMaxDistance = 60f;

    // Track active fish in the scene
    private readonly List<GameObject> alive = new List<GameObject>();
    private bool spawning;
    private float timer;

    // Object Pooling Dictionary grouped by Prefab Instance ID
    private Dictionary<EntityId, UnityEngine.Pool.ObjectPool<GameObject>> poolDictionary = new Dictionary<EntityId, UnityEngine.Pool.ObjectPool<GameObject>>();
    // Track which pool a fish belongs to so we can return it correctly
    private Dictionary<GameObject, UnityEngine.Pool.ObjectPool<GameObject>> fishToPoolMap = new Dictionary<GameObject, UnityEngine.Pool.ObjectPool<GameObject>>();

    private void Awake()
    {
        if (gameManager == null) Debug.LogError("FishSpawner: 'gameManager' is not assigned.", this);
        if (shark == null) Debug.LogError("FishSpawner: 'shark' is not assigned.", this);

        if (fishParent == null)
        {
            fishParent = new GameObject("Spawned Fish").transform;
        }

        InitializePools();
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
        
            // 1. Fetch the unique EntityId struct directly from the prefab reference
            EntityId prefabID = prefabReference.GetEntityId(); 

            // 2. Use it directly to register the pool configuration
            if (!poolDictionary.ContainsKey(prefabID))
            {
                var pool = new UnityEngine.Pool.ObjectPool<GameObject>(
                    createFunc: () => Instantiate(prefabReference, fishParent),
                    actionOnGet: (obj) => obj.SetActive(true),
                    actionOnRelease: (obj) => obj.SetActive(false),
                    actionOnDestroy: (obj) => DestroyImmediate(obj),
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
        // Clean out any references to fish that were manually destroyed or eaten by the player script
        for (int i = alive.Count - 1; i >= 0; i--)
        {
            if (alive[i] == null) alive.RemoveAt(i);
        }

        if (alive.Count >= maxAlive) return;

        FishSpecies chosen = PickSpecies();
        if (chosen == null) return;

        // 1. Correctly read the modern EntityId struct type
        EntityId prefabID = chosen.prefab.GetEntityId(); 

        // 2. Use the struct key to grab your optimized object pool instance
        if (!poolDictionary.TryGetValue(prefabID, out UnityEngine.Pool.ObjectPool<GameObject> targetPool)) return;

        // A few attempts to find a spot that isn't on top of another fish or inside the floor geometry
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Vector3 pos = RandomSpawnPosition();
            if (IsTooClose(pos)) continue;

            Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            
            // Fetch object safely from our pool instead of using Instantiate
            GameObject fish = targetPool.Get();
            fish.transform.SetPositionAndRotation(pos, rot);

            // Re-apply scale transformations natively
            fish.transform.localScale = chosen.prefab.transform.localScale;
            float scale = Random.Range(chosen.scaleRange.x, chosen.scaleRange.y);
            fish.transform.localScale *= scale;

            // Make sure the fish can be eaten, and remember which species it is.
            Edible edible = fish.GetComponent<Edible>();
            if (edible == null) edible = fish.AddComponent<Edible>();
            edible.SpeciesName = chosen.name;

            // Map the object to its originating pool so we can easily return it later
            if (!fishToPoolMap.ContainsKey(fish))
            {
                fishToPoolMap.Add(fish, targetPool);
            }

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

        // 1. Math-based horizontal calculation ahead of the shark path
        Vector3 pos = shark.position
                      + forward * Random.Range(distanceAhead.x, distanceAhead.y)
                      + right * Random.Range(-lateralRange, lateralRange);
        
        // 2. Initial free-floating vertical distribution
        pos.y = shark.position.y + verticalLead + Random.Range(-verticalJitter, verticalJitter);

        // 3. Terrain-Safety Overrides: Ensure free space checks are bounded away from geometry floor
        if (Terrain.activeTerrain != null)
        {
            float terrainFloorHeight = Terrain.activeTerrain.SampleHeight(pos) + Terrain.activeTerrain.transform.position.y;
            float safeMinimumY = terrainFloorHeight + terrainBufferHeight;

            // If calculations accidentally shoved the fish inside a rising underwater mountain, push it up into open water
            if (pos.y < safeMinimumY)
            {
                pos.y = safeMinimumY;
            }
        }

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
                DespawnFish(f);
                alive.RemoveAt(i);
            }
        } 
    }
    private void ClearAll()
    {
        for (int i = alive.Count - 1; i >= 0; i--)
        {
            if (alive[i] != null)
            {
                DespawnFish(alive[i]);
            }
        }
        alive.Clear();
    }
    private void DespawnFish(GameObject fish)
    {
    // Return back to pool instead of calling expensive Destroy() loops
        if (fishToPoolMap.TryGetValue(fish, out UnityEngine.Pool.ObjectPool<GameObject> sourcePool))
        {
            sourcePool.Release(fish);
        }
        else
        {
        // Fallback in case a foreign object sneaked into the collection tracker
            DestroyImmediate(fish);
        }
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
        Vector3 centre = shark.position + forward * (distanceAhead.x + depth * 0.5f) + Vector3.up * verticalLead;
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Gizmos.matrix = Matrix4x4.TRS(centre, Quaternion.LookRotation(forward), Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(lateralRange * 2f, verticalJitter * 2f, depth));
    }
}


/* ### What changed & How it helps your architecture

1. **`Terrain.activeTerrain.SampleHeight()` Integration:**
   Inside `RandomSpawnPosition()`, the script looks up the geometry data of your level terrain layout dynamically. If the mathematical vector lands inside a rising slope or ridge, it instantly shifts the Y point upward based on your configurable `terrainBufferHeight` slider, ensuring fish always float gracefully inside open underwater channels without getting trapped behind invisible walls or floors.
2. **`UnityEngine.Pool` System Migration:**
   The script creates an internal dictionary structure that instantiates copies once and simply turns them on/off (`SetActive(true/false)`) when entering or exiting the active gameplay boundaries. This gives you flat frame rates, completely eliminating runtime garbage collection pauses.
3. **Automatic Base Scale Retention:**
   When using object pooling, scaled values can get corrupted as objects cycle multiple times. The updated code grabs your original source prefab transforms natively to act as a reset baseline before running your localized `scaleRange` math randomizations.
### */