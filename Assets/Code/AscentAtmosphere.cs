using UnityEngine;
using UnityEngine.Rendering;

public class AscentAtmosphere : MonoBehaviour
{
    [SerializeField] private Transform shark;
    [SerializeField] private float bottomY = 0f;
    [SerializeField] private float surfaceY = 100f; // match GameManager maxHeight
    [SerializeField] private Volume deepVolume;
    [SerializeField] private Volume shallowVolume;
    [SerializeField] private Light sun;
    [SerializeField] private Vector2 sunIntensity = new Vector2(0.1f, 1.5f);
    [SerializeField] private AudioLowPassFilter lowPass; // on the audio listener / camera
    [SerializeField] private Vector2 cutoffHz = new Vector2(800f, 22000f);
    [SerializeField] private AnimationCurve curve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    private float currentT;
    private void Update()
    {
        if (shark == null) return;
        float t = curve.Evaluate(Mathf.InverseLerp(bottomY, surfaceY, shark.position.y));

        if (deepVolume) deepVolume.weight = 1f - t;
        if (shallowVolume) shallowVolume.weight = t;
        if (sun) sun.intensity = Mathf.Lerp(sunIntensity.x, sunIntensity.y, t);
        if (lowPass) lowPass.cutoffFrequency = Mathf.Lerp(cutoffHz.x, cutoffHz.y, t);
        currentT = t;
    }

    private void OnGUI()
    {
        if (shark == null) return;
        GUI.Label(new Rect(10, 10, 400, 25), $"y = {shark.position.y:F1}   t = {currentT:F2}");
    }

    
}