using UnityEngine;
using UnityEngine.Rendering.Universal;

public class CausticsScroll : MonoBehaviour
{
    public Vector2 speed = new Vector2(0.05f, 0.03f);
    UniversalAdditionalLightData data;

    void Start() => data = GetComponent<UniversalAdditionalLightData>();

    void Update()
    {
        data.lightCookieOffset += speed * Time.deltaTime;
    }
}