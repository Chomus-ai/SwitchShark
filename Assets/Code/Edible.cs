using UnityEngine;

/// <summary>
/// Marks a fish as edible. FishSpawner adds this automatically to spawned fish
/// (and sets the species name), so you only need to add it to a prefab yourself
/// if you want a custom point value for that species.
/// </summary>
public class Edible : MonoBehaviour
{
    [SerializeField] private string speciesName = "Fish";
    [SerializeField] private int points = 1;

    public string SpeciesName
    {
        get => speciesName;
        set => speciesName = value;
    }

    public int Points => points;

    public bool IsEaten { get; private set; }

    /// <summary>
    /// Eats this fish. Returns false if it was already eaten, so a fish can't
    /// be counted twice if two collision events arrive in the same frame.
    /// </summary>
    public bool TryConsume()
    {
        if (IsEaten) return false;
        IsEaten = true;
        Destroy(gameObject);
        return true;
    }
}
