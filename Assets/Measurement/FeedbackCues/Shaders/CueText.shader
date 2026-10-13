// Texto 3D de las señales: TextMeshPro (atlas SDF) con iluminación Standard (PBR) como los íconos.
//
// Por qué no los shaders de TMP: calculan el suavizado del borde con _ScreenParams (tamaño de la ventana del PC),
// que en XR no es el de la textura del ojo (3072×3072 en el VIVE): el borde se esparce varios píxeles, las letras
// nunca llegan a alpha 1 y se ven transparentes (2026-10-13, visto en el visor). Acá el ancho del borde sale de la
// derivada en pantalla (fwidth), así que es nítido en cualquier resolución y en los dos ojos (Single Pass Instanced).
//
// Bisel: la normal se inclina cerca del contorno, a partir del gradiente del SDF (misma idea que el bisel de TMP).
// Alpha: color con SrcAlpha/OneMinusSrcAlpha y alpha acumulado como cobertura (One/OneMinusSrcAlpha): el visor
// compone el passthrough con ese alpha (premultiplicado), así el interior de las letras tapa la sala por completo.
Shader "XRCollab/CueText"
{
    Properties
    {
        _MainTex ("Font Atlas", 2D) = "white" {}
        _FaceColor ("Face Color", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (0,0,0,1)
        _OutlineWidth ("Outline Width (SDF)", Range(0, 0.4)) = 0.1
        _FaceDilate ("Face Dilate (SDF)", Range(-0.3, 0.3)) = 0.05
        _BoldDilate ("Bold extra dilate (SDF)", Range(0, 0.2)) = 0.05
        _Bevel ("Bevel", Range(0, 1)) = 0.6
        _BevelWidth ("Bevel Width (SDF)", Range(0.01, 0.5)) = 0.14
        _BevelRoundness ("Bevel Roundness", Range(0, 1)) = 0.6
        _Glossiness ("Face Smoothness", Range(0, 1)) = 0.62
        _OutlineGlossiness ("Outline Smoothness", Range(0, 1)) = 0.35
        _Metallic ("Metallic", Range(0, 1)) = 0.1

        // Las propiedades de TMP que su generador de mallas y sus materiales de respaldo leen.
        _GradientScale ("Gradient Scale", Float) = 5
        _TextureWidth ("Texture Width", Float) = 512
        _TextureHeight ("Texture Height", Float) = 512
        _ScaleRatioA ("Scale RatioA", Float) = 1
        _ScaleRatioB ("Scale RatioB", Float) = 1
        _ScaleRatioC ("Scale RatioC", Float) = 1
        _WeightNormal ("Weight Normal", Float) = 0
        _WeightBold ("Weight Bold", Float) = 0.5
        _ScaleX ("Scale X", Float) = 1
        _ScaleY ("Scale Y", Float) = 1
        _PerspectiveFilter ("Perspective Correction", Range(0, 1)) = 0.875
        _VertexOffsetX ("Vertex OffsetX", Float) = 0
        _VertexOffsetY ("Vertex OffsetY", Float) = 0
        _ClipRect ("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
        _MaskSoftnessX ("Mask SoftnessX", Float) = 0
        _MaskSoftnessY ("Mask SoftnessY", Float) = 0
        _ShaderFlags ("Flags", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
        LOD 300
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

        CGPROGRAM
        #pragma surface surf Standard keepalpha vertex:vert finalcolor:final nolightmap nodirlightmap nofog noshadow exclude_path:deferred exclude_path:prepass
        #pragma target 3.5

        sampler2D _MainTex;
        float _TextureWidth, _TextureHeight, _GradientScale;
        fixed4 _FaceColor, _OutlineColor;
        float _OutlineWidth, _FaceDilate, _BoldDilate, _Bevel, _BevelWidth, _BevelRoundness;
        half _Glossiness, _OutlineGlossiness, _Metallic;

        struct Input
        {
            float2 uv_MainTex;
            float4 color : COLOR;
            float bold;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.bold = v.texcoord1.y < 0 ? 1 : 0;   // TMP marca la negrita con el signo de uv2.y
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float2 uv = IN.uv_MainTex;
            float d = tex2D(_MainTex, uv).a;                          // SDF: 0,5 = borde del glifo, más = adentro
            float edge = 0.5 - _FaceDilate - IN.bold * _BoldDilate;   // borde de la cara
            float outer = edge - _OutlineWidth;                       // borde exterior (contorno)
            float aa = max(fwidth(d), 1e-4) * 0.7;                    // 1 píxel de pantalla, en unidades SDF
            float alpha = smoothstep(outer - aa, outer + aa, d);
            float face = smoothstep(edge - aa, edge + aa, d);
            clip(alpha - 0.003);

            // Bisel: alturas en los 4 vecinos (izq, der, abajo, arriba) -> normal, como GetSurfaceNormal de TMP.
            float2 t = float2(1.0 / _TextureWidth, 1.0 / _TextureHeight);
            float4 h = float4(tex2D(_MainTex, uv - float2(t.x, 0)).a, tex2D(_MainTex, uv + float2(t.x, 0)).a,
                              tex2D(_MainTex, uv - float2(0, t.y)).a, tex2D(_MainTex, uv + float2(0, t.y)).a);
            h = saturate((h - outer) / _BevelWidth);
            h = lerp(h, sin(h * 1.5707963), _BevelRoundness);
            h *= _Bevel * _BevelWidth * _GradientScale * -2.0;
            float3 va = normalize(float3(1.0, 0.0, h.y - h.x));
            float3 vb = normalize(float3(0.0, -1.0, h.w - h.z));
            float3 n = cross(va, vb);

            float3 faceRgb = _FaceColor.rgb * IN.color.rgb;
            float3 lineRgb = _OutlineColor.rgb * lerp(float3(1, 1, 1), IN.color.rgb, 0.5);
            o.Albedo = lerp(lineRgb, faceRgb, face);
            o.Normal = -n;
            o.Metallic = _Metallic;
            o.Smoothness = lerp(_OutlineGlossiness, _Glossiness, face);
            o.Alpha = alpha * IN.color.a * _FaceColor.a;
        }

        // Pase de luces extra (el destello): suma luz solo donde hay letra, no en todo el rectángulo del glifo.
        void final(Input IN, SurfaceOutputStandard o, inout fixed4 color)
        {
        #ifdef UNITY_PASS_FORWARDADD
            color.rgb *= o.Alpha;
        #endif
        }
        ENDCG
    }
    Fallback Off
}
