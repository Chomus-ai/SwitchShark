using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Put this on the shark's ROOT object (the one with the Rigidbody).
/// When the shark's trigger collider (e.g. a "Mouth" child) touches a fish that has
/// an Edible component during the Eat state, the fish is eaten.
/// Stats reset each time Eat begins and stay readable afterwards (e.g. for the
/// summary shown during Return).
/// </summary>
public class SharkEater : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;

    [Header("Feedback (all optional)")]
    [Tooltip("Particle effect or object spawned where the fish was eaten. Destroyed after 3 seconds.")]
    [SerializeField] private GameObject eatEffectPrefab;
    [SerializeField] private AudioClip eatSound;
    [SerializeField, Range(0f, 1f)] private float eatSoundVolume = 0.8f;

    private readonly Dictionary<string, int> speciesCounts = new Dictionary<string, int>();

    public int Score { get; private set; }
    public int EatenCount { get; private set; }

    /// <summary>How many of each species were eaten this run (species name -> count).</summary>
    public IReadOnlyDictionary<string, int> SpeciesCounts => speciesCounts;

    /// <summary>Fired after each fish is eaten: (species name, points).</summary>
    public event Action<string, int> FishEaten;

    private void Awake()
    {
        if (gameManager == null)
            Debug.LogError("SharkEater: 'gameManager' is not assigned.", this);
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
            ResetStats();
    }

    private void ResetStats()
    {
        Score = 0;
        EatenCount = 0;
        speciesCounts.Clear();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (gameManager == null || gameManager.CurrentState != GameState.Eat) return;

        // The collider may be on a child of the fish, so look up the hierarchy.
        Edible edible = other.GetComponentInParent<Edible>();
        if (edible == null) return;

        // Read everything we need first, because eating destroys the fish.
        Vector3 position = edible.transform.position;
        string species = edible.SpeciesName;
        int points = edible.Points;

        if (!edible.TryConsume()) return;

        Score += points;
        EatenCount++;
        speciesCounts[species] = (speciesCounts.TryGetValue(species, out int n) ? n : 0) + 1;

        if (eatEffectPrefab != null)
            Destroy(Instantiate(eatEffectPrefab, position, Quaternion.identity), 3f);

        if (eatSound != null)
            AudioSource.PlayClipAtPoint(eatSound, position, eatSoundVolume);

        FishEaten?.Invoke(species, points);
        Debug.Log($"[SharkEater] Ate {species}. Total eaten: {EatenCount}, score: {Score}");
    }
}
