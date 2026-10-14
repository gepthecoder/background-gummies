// Gummy soft-body renderer for the URP 2D Renderer.
// Draws the polygon passed in _Pts (world space, CCW) as a glossy jelly dome: signed distance to the outline gives a
// crisp anti-aliased edge, a dome height and a smooth normal, lit by a fixed light so the highlight stays put while
// the body squishes. _Flat = 1 draws a flat silhouette instead (drop shadow).
// _Clip (world xMin, yMin, xMax, yMax) hides everything inside that rect: the buddy is hiding behind a window.
// _Clips[0.._ClipCount-1] (same layout) hide it behind the windows in front of it (BuddyLayer).
// _Patches[0.._PatchCount-1] (world centre xy, radius z, wobble phase w) sew patches of _PatchColors / _PatchRims on
// with a stitched seam: a fused monster wears its friends' colours (MonsterLook).
Shader "WhipIt/Gummy"
{
    Properties
    {
        _Color ("Color", Color) = (0.90, 0.22, 0.21, 1)
        _RimColor ("Rim Color", Color) = (0.45, 0.08, 0.08, 1)
        _LightDir ("Light Direction", Vector) = (-0.45, 0.6, 0.75, 0)
        _Offset ("Offset (xy, world)", Vector) = (0, 0, 0, 0)
        _Thickness ("Dome Thickness (world)", Float) = 0.21
        _Gloss ("Gloss", Range(1, 128)) = 48
        _Spec ("Specular", Range(0, 2)) = 0.9
        _Flat ("Flat (shadow)", Float) = 0
        _Count ("Point Count", Float) = 0
        _Clip ("Hidden Rect (world xMin yMin xMax yMax)", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define MAX_PTS 64
            float4 _Pts[MAX_PTS]; // xy = outline point in world space (set with Material.SetVectorArray)
            #define MAX_CLIPS 8
            float4 _Clips[MAX_CLIPS]; // windows in front of it (world xMin, yMin, xMax, yMax), set with Material.SetVectorArray
            float _ClipCount;
            #define MAX_PATCHES 6
            float4 _Patches[MAX_PATCHES];    // world centre xy, radius z, wobble phase w (set with Material.SetVectorArray)
            float4 _PatchColors[MAX_PATCHES];
            float4 _PatchRims[MAX_PATCHES];
            float _PatchCount;

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                float4 _LightDir;
                float4 _Offset;
                float _Thickness;
                float _Gloss;
                float _Spec;
                float _Flat;
                float _Count;
                float4 _Clip;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 ws = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.positionWS = ws.xy;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // behind a window: not drawn there
                if (_Clip.x < _Clip.z && IN.positionWS.x > _Clip.x && IN.positionWS.x < _Clip.z &&
                    IN.positionWS.y > _Clip.y && IN.positionWS.y < _Clip.w)
                    discard;
                // behind the windows in front of it
                int nc = (int)_ClipCount;
                [loop]
                for (int c = 0; c < MAX_CLIPS; c++)
                {
                    if (c >= nc) break;
                    float4 r = _Clips[c];
                    if (IN.positionWS.x > r.x && IN.positionWS.x < r.z && IN.positionWS.y > r.y && IN.positionWS.y < r.w)
                        discard;
                }

                float2 p = IN.positionWS - _Offset.xy;
                int n = (int)_Count;

                // Signed distance to the polygon (negative inside) + inverse-distance blended edge normals.
                float dmin = 1e10;
                float s = 1.0;
                float2 acc = float2(0.0, 0.0);
                [loop]
                for (int i = 0; i < MAX_PTS; i++)
                {
                    if (i >= n) break;
                    int j = (i == 0) ? n - 1 : i - 1;
                    float2 vi = _Pts[i].xy;
                    float2 vj = _Pts[j].xy;
                    float2 e = vj - vi;
                    float2 w = p - vi;
                    float2 b = w - e * saturate(dot(w, e) / max(dot(e, e), 1e-12));
                    float bb = dot(b, b);
                    dmin = min(dmin, bb);
                    acc += b * rsqrt(bb + 1e-12) / (bb * bb + 1e-10);
                    bool c1 = p.y >= vi.y;
                    bool c2 = p.y < vj.y;
                    bool c3 = e.x * w.y > e.y * w.x;
                    if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) s = -s;
                }
                float d = s * sqrt(dmin);
                float aa = max(fwidth(d), 1e-5);
                float alpha = saturate(0.5 - d / aa);

                if (_Flat > 0.5)
                    return half4(_Color.rgb, _Color.a * alpha);

                // Dome: height rises from the rim inwards; normal tilts outward near the rim.
                float inside = saturate(-d / _Thickness);
                float hgt = sqrt(saturate(1.0 - (1.0 - inside) * (1.0 - inside)));
                float side = sqrt(saturate(1.0 - hgt * hgt));
                float2 n2 = s * acc;
                n2 *= rsqrt(max(dot(n2, n2), 1e-20));
                float3 N = normalize(float3(n2 * side, hgt));

                float3 L = normalize(_LightDir.xyz);
                float3 H = normalize(L + float3(0.0, 0.0, 1.0));
                float diff = saturate(dot(N, L));
                float spec = pow(saturate(dot(N, H)), _Gloss) * _Spec;

                // patches sewn on (a fused monster): wobbly edges, a seam and little cross stitches over it
                float3 baseC = _Color.rgb, rimC = _RimColor.rgb;
                float stitch = 0.0;
                int np = (int)_PatchCount;
                [loop]
                for (int k = 0; k < MAX_PATCHES; k++)
                {
                    if (k >= np) break;
                    float4 pc = _Patches[k];
                    float2 dp = p - pc.xy;
                    float r = length(dp);
                    float a = atan2(dp.y, dp.x);
                    float R = pc.z * (1.0 + 0.10 * sin(5.0 * a + pc.w) + 0.05 * sin(9.0 * a + 2.0 * pc.w));
                    float e = R - r; // > 0 inside the patch
                    float ea = max(fwidth(e), 1e-5);
                    float m = saturate(0.5 + e / ea);
                    baseC = lerp(baseC, _PatchColors[k].rgb, m);
                    rimC = lerp(rimC, _PatchRims[k].rgb, m);
                    float seam = saturate(1.0 - abs(e) / (ea * 1.2 + _Thickness * 0.015));
                    float tick = abs(frac(a * R / (_Thickness * 0.32)) - 0.5);           // along the seam
                    float xst = saturate((0.13 - tick) / max(fwidth(a * R / (_Thickness * 0.32)), 1e-5)) *
                                  saturate((_Thickness * 0.11 - abs(e)) / ea);             // across it, short
                    stitch = max(stitch, max(seam * 0.8, xst));
                }

                float3 col = baseC * (0.6 + 0.45 * diff);
                col = lerp(rimC, col, smoothstep(0.0, 0.3, inside));
                col += baseC * 0.12 * inside + spec;
                col = lerp(col, rimC * 0.45, stitch * 0.85);
                return half4(saturate(col), _Color.a * alpha);
            }
            ENDHLSL
        }
    }
}
