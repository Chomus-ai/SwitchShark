using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// The "you made it" summary shown in the ending cutscene. When the game enters Surface it
/// reads the run's stats from SharkEater (which keeps them after Rise ends) and writes them
/// into a text box. Showing/fading the panel itself is done by the ending cutscene (a Card
/// pointing at this object's CanvasGroup), so this script only supplies the words.
///
/// Testable on its own: assign the references, press Play, then call Refresh from the
/// component's context menu (right-click header > Refresh) to see the text.
/// </summary>
public class EndCard : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;
    [SerializeField] private SharkEater eater;

    [Header("Text")]
    [SerializeField] private TMP_Text stats;
    [Tooltip("How many species lines to list, most-eaten first.")]
    [SerializeField] private int maxSpeciesLines = 6;
    [SerializeField] private string nothingEatenMessage = "You didn't catch any fish this time.";

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
        if (next == GameState.Surface) Refresh();
    }

    [ContextMenu("Refresh")]
    public void Refresh()
    {
        if (eater == null || stats == null) return;

        var sb = new StringBuilder();

        if (eater.EatenCount == 0)
        {
            sb.Append(nothingEatenMessage);
        }
        else
        {
            sb.AppendLine($"Fish eaten: {eater.EatenCount}");
            sb.AppendLine($"Score: {eater.Score}");
            sb.AppendLine();

            foreach (var entry in eater.SpeciesCounts
                         .OrderByDescending(e => e.Value)
                         .ThenBy(e => e.Key)
                         .Take(Mathf.Max(1, maxSpeciesLines)))
            {
                sb.AppendLine($"{entry.Key}: {entry.Value}");
            }
        }

        stats.text = sb.ToString().TrimEnd();
    }
}
