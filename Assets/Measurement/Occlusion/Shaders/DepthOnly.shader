// Oclusor: escribe solo profundidad (nada de color ni alpha). Lo virtual que quede detrás no se dibuja y en ese
// lugar se sigue viendo el passthrough (la mano real, un mueble). Se dibuja antes que todo lo demás.
Shader "XRCollab/DepthOnly"
{
    SubShader
    {
        Tags { "Queue" = "Geometry-50" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        ColorMask 0
        ZWrite On
        ZTest LEqual
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }
    }
    Fallback Off
}
