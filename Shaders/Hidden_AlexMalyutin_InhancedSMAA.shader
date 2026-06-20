Shader "Hidden/AlexMalyutin/EnhancedSMAA"
{
    Properties
    {
        [HideInInspector] _StencilRef("_StencilRef", Int) = 4
        [HideInInspector] _StencilMask("_StencilMask", Int) = 4
    }

    HLSLINCLUDE
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
    #pragma multi_compile SMAA_PRESET_LOW SMAA_PRESET_MEDIUM SMAA_PRESET_HIGH SMAA_PRESET_ULTRA
    #pragma editor_sync_compilation
    ENDHLSL

    SubShader
    {
        // Edge Detection
        UsePass "Hidden/PostProcessing/SubpixelMorphologicalAntialiasing/EDGE DETECTION"

        // Blend Weights Calculation
        Pass
        {
            Name "Blend Weights Calculation"
            Stencil
            {
                WriteMask[_StencilMask]
                ReadMask [_StencilMask]
                Ref [_StencilRef]
                Comp Equal
                Pass Replace
            }

            HLSLPROGRAM

                #pragma vertex VertBlend
                #pragma fragment FragBlendCustom
                #include "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/SubpixelMorphologicalAntialiasingBridge.hlsl"

                float4 _SubsampleIndices;

                float4 FragBlendCustom(VaryingsBlend i) : SV_Target
                {
                    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                    return SMAABlendingWeightCalculationPS(i.texcoord, i.pixcoord, i.offsets, _InputTexture, _AreaTex, _SearchTex, _SubsampleIndices);
                }

            ENDHLSL
        }

        // Neighborhood Blending
        Pass
        {
            Name "Neighborhood Blending"
            HLSLPROGRAM

            #pragma vertex VertNeighbor
            #pragma fragment FragNeighbor2

            #pragma multi_compile _ SMAA_REPROJECTION SMAA_UV_BASED_REPROJECTION

            float4x4 _ReprojectionMatrix;
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/SubpixelMorphologicalAntialiasingBridge.hlsl"

            SMAATexture2D(_VelocityTex);

            float4 FragNeighbor2(VaryingsNeighbor i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return SMAANeighborhoodBlendingPS(
                    i.texcoord, i.offset, _InputTexture, _BlendTex
                #if SMAA_REPROJECTION
                    , _VelocityTex
                #endif
                );
            }
            ENDHLSL
        }
        
        Pass
        {
            Name "SMAATemporalResolve"
            Cull Off
            ZTest Off
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex VertTemporalResolve
            #pragma fragment FragTemporalResolve
            
            #pragma multi_compile _ SMAA_REPROJECTION SMAA_UV_BASED_REPROJECTION

            float4x4 _ReprojectionMatrix;
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/SubpixelMorphologicalAntialiasingBridge.hlsl"

            float4 _InputTexture2_TexelSize;
            TEXTURE2D_X(_InputTexture2);

            struct VaryingsTemporalResolve
            {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };
            
            VaryingsTemporalResolve VertTemporalResolve(Attributes v)
            {
                VaryingsTemporalResolve o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = GetFullScreenTriangleVertexPosition(v.vertexID);
                o.texcoord = GetFullScreenTriangleTexCoord(v.vertexID);
                return o;
            }

            float4 FragTemporalResolve(VaryingsTemporalResolve i) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return SMAAResolvePS(
                    i.texcoord, _InputTexture, _InputTexture2
                #if SMAA_REPROJECTION
                    , _VelocityTex
                #endif
                );
            }
            ENDHLSL
        }
    }
}