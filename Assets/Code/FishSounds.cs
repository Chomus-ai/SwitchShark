using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class MobAudio : MonoBehaviour
{
    private AudioSource mobAudioSource;
    [SerializeField] private AudioClip movementSound;

    private void Awake()
    {
        mobAudioSource = GetComponent<AudioSource>();
        
        // Configure the AudioSource automatically for 3D Spatial Audio
        mobAudioSource.playOnAwake = false;
        mobAudioSource.spatialBlend = 1.0f; // 1.0 means full 3D sound
        mobAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        mobAudioSource.minDistance = 1f;    // Full volume when within 1 meter
        mobAudioSource.maxDistance = 50f;   // Completely silent past 50 meters
    }

    // Call this function exactly when the mob takes a step or moves
    public void PlayMovementSound()
    {
        if (movementSound != null)
        {
            mobAudioSource.PlayOneShot(movementSound);
        }
    }
}
