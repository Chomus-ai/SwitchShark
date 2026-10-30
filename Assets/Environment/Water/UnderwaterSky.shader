// Underwater background (skybox). The horizon matches the fog colour so distant seabed
// fades into it seamlessly; it brightens toward the surface and darkens toward the abyss.
// Looking up from shallow water you see Snell's window: a bright, rippling circle of sky
// straight overhead (about 48 degrees from vertical) with the sun in it.
// Colours and window strength are driven per depth zone by UnderwaterAtmosphere.
Shader "SwitchShark/Underwater Sky"
{
    Properties
    {
        _TopColor ("Toward the surface", Color) = (0.45, 0.75, 0.82, 1)
        _HorizonColor ("Horizon (match the fog)", Color) = (0.12, 0.46, 0.54, 1)
        _BottomColor ("Toward the abyss", Color) = (0.03, 0.12, 0.16, 1)
        _UpCurve ("Brightening curve", Range(0.2, 4)) = 1.2
        _DownCurve ("Darkening curve", Range(0.2, 4)) = 0.8
        _SunColor ("Sun glow", Color) = (0.8, 0.95, 1, 1)
        _SunDirection ("Direction toward the sun", Vector) = (0.2, 1, 0.3, 0)
        _SunSize ("Sun glow tightness", Range(1, 64)) = 10
        _SunIntensity ("Sun glow strength", Range(0, 2)) = 0.6

        [Header(Snells window)]
        _WindowStrength ("Window strength (0 in the deep)", Range(0, 1)) = 1
        _WindowZenith ("Sky colour overhead", Color) = (0.48, 0.76, 0.92, 1)
        _WindowEdge ("Sky colour at the window edge", Color) = (0.72, 0.9, 0.97, 1)
        _WindowSize ("Window edge (cosine of its angle)", Range(0.3, 0.95)) = 0.66
        _RippleScale ("Ripple size", Range(0.5, 20)) = 5
        _RippleSpeed ("Ripple speed", Range(0, 3)) = 0.6
        _RippleStrength ("Ripple wobble", Range(0, 0.15)) = 0.05
        _Caustics ("Shimmer brightness", Range(0, 1)) = 0.22
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _TopColor, _HorizonColor, _BottomColor, _SunColor, _WindowZenith, _WindowEdge;
            float4 _SunDirection;
            float _UpCurve, _DownCurve, _SunSize, _SunIntensity;
            float _WindowStrength, _WindowSize, _RippleScale, _RippleSpeed, _RippleStrength, _Caustics;

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            // Smooth wave pattern for the moving surface, as seen from below.
            float waves(float2 p, float t)
            {
                float w = sin(p.x * 1.3 + t) + sin(p.y * 1.7 - t * 1.2)
                        + sin((p.x + p.y) * 0.9 + t * 0.7) + sin((p.x - p.y) * 2.3 - t * 1.6) * 0.5;
                return w / 3.5;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = saturate(d.y), down = saturate(-d.y);

                // Water: horizon = fog colour, lighter above, darker below.
                float3 col = lerp(_HorizonColor.rgb, _TopColor.rgb, pow(up, _UpCurve));
                col = lerp(col, _BottomColor.rgb, pow(down, _DownCurve));

                float3 sunDir = normalize(_SunDirection.xyz);
                float sun = saturate(dot(d, sunDir));
                col += _SunColor.rgb * pow(sun, _SunSize) * _SunIntensity * up;

                // Snell's window: sky seen through the surface, wobbling with the waves.
                if (_WindowStrength > 0.001 && d.y > 0.01)
                {
                    float t = _Time.y * _RippleSpeed;
                    float2 p = d.xz / d.y * _RippleScale;           // where this ray meets the surface
                    float w = waves(p, t);
                    float edge = _WindowSize + w * _RippleStrength;
                    float window = smoothstep(edge - 0.03, edge + 0.05, d.y);

                    float3 sky = lerp(_WindowEdge.rgb, _WindowZenith.rgb, saturate((d.y - edge) / (1 - edge)));
                    sky += _SunColor.rgb * pow(sun, _SunSize * 3) * 1.5;          // the sun itself
                    float shimmer = pow(saturate(1 - abs(waves(p * 1.9, t * 1.3))), 6); // bright wave crests
                    sky += _Caustics * shimmer;

                    // Outside the window the surface reflects the water below, slightly brighter at the rim.
                    float rim = smoothstep(edge - 0.18, edge, d.y) * (1 - window);
                    col += _WindowEdge.rgb * rim * 0.15 * _WindowStrength;
                    col = lerp(col, sky, window * _WindowStrength);
                }
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
