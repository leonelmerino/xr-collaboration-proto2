// Efectos de las señales 3D sobre el passthrough (brillos, rayos, ondas, chispas, confeti).
// Sin iluminación, color = textura × color del vértice × _Color × _Intensity.
// Mezcla configurable desde el material:
//   - Aditivo "luz" (Blend One One, ColorMask RGB): suma luz a la imagen de la sala sin tocar el alpha, así el
//     visor (capa con alpha premultiplicado) deja ver la sala detrás y le agrega el brillo, como un bloom real.
//   - Alpha (Blend SrcAlpha OneMinusSrcAlpha, ColorMask RGBA): confeti y polvo opacos que tapan la sala.
Shader "XRCollab/CueFx"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Intensity ("Intensity", Float) = 1
        _Premul ("Premultiply RGB by alpha (1 additive, 0 alpha)", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 1
        _ColorMask ("Color Mask (14 = RGB, 15 = RGBA)", Float) = 14
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Blend [_SrcBlend] [_DstBlend]
        ColorMask [_ColorMask]
        ZWrite [_ZWrite]
        ZTest LEqual
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Intensity;
            float _Premul;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                c.rgb *= _Intensity;
                // En aditivo la textura trae el brillo en su alpha: premultiplicar para que el borde se apague suave.
                c.rgb *= lerp(1, c.a, _Premul);
                return c;
            }
            ENDCG
        }
    }
}
