// PropHunt play-area boundary wall shader.
//
// Applied to a runtime-built open cylinder mesh (see PlayArea/BorderMesh.cs and PlayArea/PlayAreaBorder.cs).
// The cylinder is double-sided (Cull Off) so the player sees the wall from inside AND outside the play area.
//
// Why plain CG (not URP HLSL includes): same rationale as PropOutline.shader - the exact URP package version is
// not pinnable from the game install and IL2CPP cannot compile ShaderLab at runtime. A plain CG pass with
// UnityCG.cginc has no URP package coupling, compiles in any 2022.3 editor, and URP renders it via SRPDefaultUnlit.
//
// Why ZTest LessEqual (not ZTest Always like PropOutline): this cylinder lives in world space and must be occluded
// by buildings and terrain.
//
// _Proximity is written from C# each frame: 0 = player is at the wall edge -> full _AlphaNear; 1 = far inside -> _AlphaBase.
//
// The previous version used UNITY_DECLARE_DEPTH_TEXTURE + SAMPLE_DEPTH_TEXTURE_PROJ + COMPUTE_EYEDEPTH for a
// soft depth-intersection effect. Those are Built-in RP macros that fail under URP at runtime, causing the entire
// shader to be replaced by the magenta error shader. Soft intersection has been removed; all other visuals are
// unchanged. This shader now touches only the same URP-safe builtins used by PropOutline.shader.
Shader "PropHunt/Border"
{
    Properties
    {
        [Header(Color)]
        _WallColor    ("Wall Color",       Color) = (1.0, 0.25, 0.10, 0.45)
        _RimColor     ("Rim Color (edge)", Color) = (1.0, 0.55, 0.20, 1.00)

        [Header(Fresnel Rim)]
        _RimPower     ("Rim Power",     Range(0.5, 12)) = 3.5
        _RimIntensity ("Rim Intensity", Range(0, 8))    = 3.2

        [Header(Vertical Gradient)]
        _GroundFade   ("Ground Fade (UV)", Range(0.001, 0.15)) = 0.03
        _TopFade      ("Top Fade (UV)",    Range(0.001, 0.25)) = 0.07

        [Header(Scroll Bands)]
        _ScrollSpeed  ("Scroll Speed (UV/s)", Range(-4, 4))  = 0.20
        _BandFreq     ("Band Frequency",      Range(0, 20))  = 5.0
        _BandContrast ("Band Contrast",       Range(0, 1))   = 0.22

        [Header(Proximity)]
        _Proximity    ("Proximity (0=near 1=far)", Range(0, 1)) = 1.0
        _AlphaBase    ("Base Alpha (far away)",    Range(0, 0.3))  = 0.03
        _AlphaNear    ("Near Alpha (close)",       Range(0, 1))    = 0.65
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent+10"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
            "RenderPipeline"  = "UniversalPipeline"
        }

        Pass
        {
            Name "BorderWall"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Off
            ZTest LEqual
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _WallColor;
            fixed4 _RimColor;
            float  _RimPower;
            float  _RimIntensity;
            float  _GroundFade;
            float  _TopFade;
            float  _ScrollSpeed;
            float  _BandFreq;
            float  _BandContrast;
            float  _Proximity;
            float  _AlphaBase;
            float  _AlphaNear;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldNrm : TEXCOORD0;
                float3 viewDir  : TEXCOORD1;
                float2 uv       : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNrm = UnityObjectToWorldNormal(v.normal);
                o.viewDir  = normalize(_WorldSpaceCameraPos - wp);
                o.uv       = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n  = normalize(i.worldNrm);
                float3 vd = normalize(i.viewDir);

                // Fresnel rim; abs() handles Cull Off back-faces symmetrically.
                float ndotv = saturate(abs(dot(vd, n)));
                float fres  = pow(1.0 - ndotv, max(_RimPower, 0.5));

                // Vertical gradient: fade in from the ground, out near the top.
                // uv.y = 0 at the ground ring, 1 at the top ring (set by BorderMesh.cs).
                float vGrad = smoothstep(0.0, _GroundFade, i.uv.y) *
                              smoothstep(1.0, 1.0 - _TopFade, i.uv.y);

                // Scrolling horizontal bands (energy/force-field feel).
                // uv.x = 0..1 around the ring (set by BorderMesh.cs).
                float bandU = i.uv.x + _Time.y * _ScrollSpeed;
                float band  = 0.5 + 0.5 * sin(bandU * _BandFreq * 6.28318);
                float bandMask = lerp(1.0, band, _BandContrast);

                // Proximity alpha: 0 = at the wall -> _AlphaNear, 1 = far -> _AlphaBase.
                // _Proximity is set each frame by PlayAreaBorder.cs.
                float proxAlpha = lerp(_AlphaNear, _AlphaBase, saturate(_Proximity));

                fixed3 col = lerp(_WallColor.rgb, _RimColor.rgb, saturate(fres));

                float rimA  = saturate(fres * _RimIntensity) * _RimColor.a;
                float baseA = _WallColor.a * proxAlpha;
                float a     = saturate((rimA + baseA) * vGrad * bandMask);

                return fixed4(col, a);
            }
            ENDCG
        }
    }

    Fallback "Universal Render Pipeline/Unlit"
}
