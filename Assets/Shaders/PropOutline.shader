// "Becomable prop" selection highlight for PropHunt.
//
// Applied to a render-only CLONE of the prop's own mesh (child "ph_hl", localScale=1,
// identity transform - see PropHighlighter.AddShellGo). There is NO extra geometry and
// NO inverted-hull pass: that is what caused the dalmatian speckle on previous versions
// (coincident faces + hull extrusion -> z-fighting). This shader is speckle-free by
// construction: one pass, no depth compare (ZTest Always), no second mesh.
//
// Why alpha-blend instead of additive:
//   Additive (Blend SrcAlpha One) adds a small value on top of an already-bright daylight
//   scene pixel. Against near-white lit surfaces the delta is invisible. Alpha-blend
//   (Blend SrcAlpha OneMinusSrcAlpha) OVERLAYS a saturated tinted color regardless of
//   background brightness: even a 50% cyan tint reads clearly in full sun.
//
// Why plain CG and not a URP HLSL shader:
//   The game is URP but the exact URP package version is not pinnable from the install,
//   and IL2CPP cannot compile ShaderLab at runtime. A plain CG pass has no URP include
//   coupling, compiles once in any 2022.3 editor (no URP package required), and URP
//   renders it via the SRPDefaultUnlit pass path. This keeps the shipped bundle
//   version-independent.
//
// Every property has a sensible default so the material looks right even if code sets
// nothing; PropHunt tunes them at runtime (no bundle rebuild needed to adjust feel).
Shader "PropHunt/PropOutline"
{
    Properties
    {
        [Header(Color)]
        _OutlineColor ("Highlight Color", Color)   = (0.10, 0.85, 1.0, 0.55)
        _RimColor     ("Rim Color (edge)", Color)  = (0.50, 1.00, 1.0, 0.90)

        [Header(Fresnel Rim)]
        _RimPower     ("Rim Power",     Range(0.25, 12))  = 2.2
        _RimIntensity ("Rim Intensity", Range(0, 8))      = 3.5
        _CoreAlpha    ("Body Fill Alpha", Range(0, 1))    = 0.20

        [Header(Pulse)]
        _PulseSpeed   ("Pulse Speed",  Range(0, 12))   = 2.2
        _PulseAmount  ("Pulse Amount", Range(0, 1))    = 0.25

        // Kept for loader/material compatibility with existing call sites; unused by this pass.
        _OutlineWidth ("Outline Width (unused)", Range(0, 12)) = 0
        _FillAlpha    ("Fill Alpha (unused)",    Range(0, 1))  = 0
        _CoreGlow     ("Core Glow (unused)",     Range(0, 2))  = 0
        _RimIntensity2("Rim Intensity 2 (unused)", Range(0, 8)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"          = "Overlay"
            "RenderType"     = "Overlay"
            "IgnoreProjector" = "True"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "RimHighlight"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Back
            ZTest Always     // no depth compare -> cannot z-fight the coincident prop mesh
            ZWrite Off
            // Alpha-blend: overlays the tinted highlight color over the scene regardless of
            // scene brightness. Visible in bright daylight; additive was not.
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _OutlineColor;
            fixed4 _RimColor;
            float  _RimPower;
            float  _RimIntensity;
            float  _CoreAlpha;
            float  _PulseSpeed;
            float  _PulseAmount;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldNrm : TEXCOORD0;
                float3 viewDir  : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNrm = UnityObjectToWorldNormal(v.normal);
                o.viewDir  = normalize(_WorldSpaceCameraPos - worldPos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n  = normalize(i.worldNrm);
                float3 vd = normalize(i.viewDir);

                // Fresnel: ~1 at the silhouette (grazing angle), ~0 facing the camera.
                float fres = pow(1.0 - saturate(dot(vd, n)), max(_RimPower, 0.25));

                // Gentle pulse so the highlight reads as "alive" and draws the eye.
                float pulse = 1.0 + _PulseAmount * sin(_Time.y * _PulseSpeed);

                // Color: blend from body highlight to the brighter rim color at the edge.
                fixed3 col = lerp(_OutlineColor.rgb, _RimColor.rgb, saturate(fres));

                // Alpha: edge gets full rim intensity; body gets the constant fill.
                // Both are pulsed. Saturate so we stay in [0,1].
                float rimA  = saturate(fres * _RimIntensity * pulse) * _RimColor.a;
                float bodyA = _CoreAlpha * pulse * _OutlineColor.a;
                float a     = saturate(rimA + bodyA);

                return fixed4(col, a);
            }
            ENDCG
        }
    }

    // URP fallback: if the SRPDefaultUnlit pass is skipped for any reason, fall back to the
    // URP Unlit shader so the shell renders as a flat white/tinted mesh rather than invisible.
    Fallback "Universal Render Pipeline/Unlit"
}
