// SpriteHsvShift.shader
// HeroDefense 表现层 — Sprite HSV 偏移 shader（怪物 = 武将/兵种暗黑变体）
//
// 用途（T237）：
//   怪物运行时复用对应武将/兵种的美术，再做 HSV 色调偏移把"光明武将"变成"黑暗怪物"。
//   一套美术资产复用两种语义，淘汰独立的 monster/ 美术。
//
// 用法：
//   1. C# 给怪物 SpriteRenderer 换上用本 shader 的 Material
//        var mat = new Material(Shader.Find("HeroDefense/SpriteHsvShift"));
//   2. 设三个 HSV uniform：
//        mat.SetFloat("_HueShift",   degrees);   // 色相偏移（度，-360..360）
//        mat.SetFloat("_Saturation", mul);       // 饱和度乘子（0..2，<1 去色）
//        mat.SetFloat("_Brightness", mul);       // 亮度乘子（0..2，<1 暗化）
//   3. dark_color_shift 'shift_hue:60,saturation:0.7,brightness:0.4'
//      → _HueShift=60, _Saturation=0.7, _Brightness=0.4
//
// 设计要点：
//   - 在 SpriteHitFlash.shader 基础上加 HSV 偏移段；同时保留 _FlashColor/_FlashAmount，
//     因此打了暗化 material 的怪物仍能被 HitFeedback.CoFlash 闪白（同一 shader 双职责）。
//   - frag 顺序：采样 → HSV 偏移 → 闪白 lerp → 预乘 alpha。alpha 全程不变。
//   - RGB↔HSV 转换用 Sam Hocevar 无分支算法（WebGL 1.0/2.0 友好）。
//
// 兼容性（CLAUDE.md §10 / R-V14）：
//   - built-in 渲染管线语法 + URP 兼容子集；WebGL 1.0/2.0 可编译。
//   - 不用 SV_VertexID / multi_compile_instancing。

Shader "HeroDefense/SpriteHsvShift"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        // HSV 偏移（怪物暗化）
        _HueShift ("Hue Shift (deg)", Range(-360,360)) = 0
        _Saturation ("Saturation Mul", Range(0,2)) = 1
        _Brightness ("Brightness Mul", Range(0,2)) = 1
        // 闪白（HitFeedback 复用 — 暗化怪物受击仍能闪白）
        _FlashColor ("Flash Color", Color) = (1,1,1,1)
        _FlashAmount ("Flash Amount", Range(0,1)) = 0
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            fixed4 _FlashColor;
            float  _FlashAmount;
            float  _HueShift;
            float  _Saturation;
            float  _Brightness;
            fixed4 _RendererColor;
            fixed2 _Flip;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                IN.vertex.xy *= _Flip.xy;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.texcoord = IN.texcoord;
                OUT.color = IN.color * _Color * _RendererColor;

                #ifdef PIXELSNAP_ON
                OUT.vertex = UnityPixelSnap(OUT.vertex);
                #endif

                return OUT;
            }

            sampler2D _MainTex;
            sampler2D _AlphaTex;
            float _EnableExternalAlpha;

            fixed4 SampleSpriteTexture (float2 uv)
            {
                fixed4 color = tex2D(_MainTex, uv);

                #if ETC1_EXTERNAL_ALPHA
                fixed4 alpha = tex2D(_AlphaTex, uv);
                color.a = lerp(color.a, alpha.r, _EnableExternalAlpha);
                #endif

                return color;
            }

            // RGB → HSV（Sam Hocevar 无分支算法）
            float3 RgbToHsv(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            // HSV → RGB
            float3 HsvToRgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                fixed4 c = SampleSpriteTexture(IN.texcoord) * IN.color;

                // HSV 偏移（怪物暗化）。色相偏移度 → 0..1 圈数；饱和度 / 亮度乘子。
                float3 hsv = RgbToHsv(c.rgb);
                hsv.x = frac(hsv.x + _HueShift / 360.0);
                hsv.y = saturate(hsv.y * _Saturation);
                hsv.z = saturate(hsv.z * _Brightness);
                c.rgb = HsvToRgb(hsv);

                // 闪白（HitFeedback 复用）：lerp(rgb, _FlashColor.rgb, _FlashAmount)
                c.rgb = lerp(c.rgb, _FlashColor.rgb, _FlashAmount);

                c.rgb *= c.a;
                return c;
            }
        ENDCG
        }
    }

    Fallback "Sprites/Default"
}

