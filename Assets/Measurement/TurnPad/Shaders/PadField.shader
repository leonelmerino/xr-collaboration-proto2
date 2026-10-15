// Botón de turno «field»: puntos de luz que fluyen por las líneas de campo de un imán (dipolo) puesto en el centro
// del botón, como limaduras de hierro. Todo se calcula aquí (una malla fija de quads con semillas, un draw call):
// el CPU solo pasa _Color, _Near, _Hold y _Hand. Aditivo (suma luz a la sala): no tapa nada, así que no necesita
// oclusión por la mano.
// Espacio del objeto = el del botón: -Z es «arriba» (la cara mira al participante), el plano z = 0 es la mesa.
Shader "XRCollab/PadField"
{
    Properties
    {
        _Color ("Color", Color) = (0.11, 0.69, 0.48, 1)
        _Intensity ("Intensity", Float) = 1.6
        _Near ("Hand nearness 0..1", Float) = 0
        _Hold ("Hold progress 0..1", Float) = 0
        _Hand ("Fingertip (object space) + strength", Vector) = (0, 0, 0, 0)
        _Flash ("Toggle flash 0..1", Float) = 0
        _Radius ("Outline radius (m)", Float) = 0.0515
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+12" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One One
        ColorMask RGB
        ZWrite Off
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

            fixed4 _Color;
            float _Intensity, _Near, _Hold, _Flash, _Radius;
            float4 _Hand;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 corner : TEXCOORD0;   // -1..1 esquina del quad
                float4 seed : TEXCOORD1;     // 4 números al azar por punto
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 corner : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float4 s = v.seed;
                float t = _Time.y;
                float energy = 1 + 1.6 * _Near + 2.5 * _Hold;

                // Línea de campo de un dipolo vertical: r = L sin²θ. θ va del eje (sale del centro) al plano de la mesa
                // (cae en el borde, a distancia L): solo la mitad de arriba, lo de abajo quedaría bajo la mesa.
                float L = lerp(0.028, 0.085, s.x * s.x) * lerp(1, 0.82, _Near);
                float u = frac(s.z + t * (0.07 + 0.11 * s.w) * energy);
                float theta = lerp(0.32, 1.5708, u);
                float sn = sin(theta);
                float r = L * sn * sn;
                float phi = 6.28318 * s.y + t * 0.1 * energy * (s.w > 0.5 ? 1 : -1) + 0.15 * sin(t * 0.7 + s.x * 9);
                float3 p = float3(r * sn * cos(phi), r * sn * sin(phi), -r * cos(theta) * 1.1);

                // Mantener: los puntos se juntan sobre el contorno (el anillo se «carga»).
                float3 ring = float3(_Radius * cos(phi), _Radius * sin(phi), -0.001);
                p = lerp(p, ring, smoothstep(0, 1, _Hold) * 0.85);

                // Imán en la punta del dedo: cerca de ella los puntos se le acercan.
                float3 d = _Hand.xyz - p;
                float pull = _Hand.w * saturate(1 - length(d) / 0.06);
                p += d * pull * pull * 0.55;

                // Brillo: aparece y se apaga a lo largo de la línea + titileo.
                float life = sin(3.14159 * u);
                float twinkle = 0.55 + 0.45 * sin(t * (2.5 + 6 * s.x) + s.y * 40);
                float a = life * twinkle * (0.55 + 0.45 * _Near + 0.6 * _Hold) + _Flash * 1.5;

                float size = (0.0011 + 0.0011 * s.w) * (1 + 0.6 * _Hold + _Flash);
                float3 vc = UnityObjectToViewPos(float4(p, 1));
                vc.xy += v.corner * size;
                o.pos = mul(UNITY_MATRIX_P, float4(vc, 1));
                o.corner = v.corner;
                // Mezcla con blanco: el núcleo de cada punto es casi blanco, el halo del color del estado.
                o.color = fixed4(lerp(_Color.rgb, 1, 0.45 + 0.3 * twinkle), a);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float q = saturate(1 - dot(i.corner, i.corner));
                q *= q;
                return fixed4(i.color.rgb * q * i.color.a * _Intensity, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
