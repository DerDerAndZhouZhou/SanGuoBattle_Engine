// SpriteHitFlash.shader
// HeroDefense 表现层 — Sprite 闪白 shader（打击四件套之一）
//
// 用法：
//   1. Material 选用 Shader = "HeroDefense/SpriteHitFlash"
//   2. C# HitFeedback.CoFlash 协程驱动：
//        mat.SetColor("_FlashColor", Color.white);
//        mat.SetFloat("_FlashAmount", 1f);  // 0→1→0 over 80ms（hit_flash_duration）
//
// 设计要点：
//   - 沿用 Unity 内置 Sprites-Default 基础结构（兼容 SpriteRenderer + Texture Atlas）
//   - 加 _FlashColor (Color) + _FlashAmount (Range 0-1) 两个 uniform
//   - frag 阶段：lerp(spriteColor.rgb, _FlashColor.rgb, _FlashAmount)
//   - alpha 不变（避免闪白同时透明）
//   - URP 2D Lit fallback 在 URP 项目 Tuanjie 1.8.4 下走 SpritesDefault 子集即可
//
// 兼容性（AGENTS.md §10 / R-V14）：
//   - 该 shader 使用 built-in 渲染管线语法 + URP 兼容子集
//   - WebGL 1.0 / 2.0 都能编译（不用 SV_VertexID / multi_compile_instancing）
//   - 若 Tuanjie 1.8.4 URP 2D 报错，回退方案：HitFeedback.CoFlash 改用 SpriteRenderer.color 双缓冲

Shader "HeroDefense/SpriteHitFlash"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
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

            fixed4 frag(v2f IN) : SV_Target
            {
                fixed4 c = SampleSpriteTexture(IN.texcoord) * IN.color;
                // 关键 — 闪白：lerp(rgb, _FlashColor.rgb, _FlashAmount)；alpha 保持不变
                c.rgb = lerp(c.rgb, _FlashColor.rgb, _FlashAmount);
                c.rgb *= c.a;
                return c;
            }
        ENDCG
        }
    }

    Fallback "Sprites/Default"
}

