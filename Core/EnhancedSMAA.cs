using AlexMalyutin.EnhancedSMAA.InternalBridge;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace AlexMalyutin.EnhancedSMAA
{
    public class EnhancedSMAA : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        private const int EdgeDetectionPassId = 0;
        private const int BlendWeightsCalculationPassId = 1;
        private const int NeighborhoodBlendingPassId = 2;
        private const int TemporalResolvePassId = 3;

        private const int SMAAHistoryBufferId = 1042;

        public static readonly int _InputTexture = Shader.PropertyToID("_InputTexture");
        public static readonly int _InputTexture2 = Shader.PropertyToID("_InputTexture2");
        public static readonly int _SMAAAreaTex = Shader.PropertyToID("_AreaTex");
        public static readonly int _SMAASearchTex = Shader.PropertyToID("_SearchTex");
        public static readonly int _SMAABlendTex = Shader.PropertyToID("_BlendTex");
        public static readonly int _SMAARTMetrics = Shader.PropertyToID("_SMAARTMetrics");

        public BoolParameter Active = new(true);
        public EnumParameter<EnhancedSMAAQualityLevel> SMAAQuality = new(EnhancedSMAAQualityLevel.Medium);

        public EnumParameter<EnhancedSMAAMode> SMAAMode = new(EnhancedSMAAMode.SMAA_1x);

        // TODO: Use textures from HRDP asset!
        public TextureParameter SMAAAreaTex = new(null);
        public TextureParameter SMAASearchTex = new(null);

        [SerializeReference] private Shader _shader;

        private Material _material;
        private MaterialPropertyBlock _props;

        private RTHandle _edgeTex;
        private RTHandle _blendTex;
        private BufferedRTHandleSystem _system;

        private Vector4[] _subsampleIndices2 = new Vector4[2]
        {
            new(1.0f, 1.0f, 1.0f, 0.0f),
            new(2.0f, 2.0f, 2.0f, 0.0f),
        };
        private Vector4[] _subsampleIndices4 = new Vector4[4]
        {
            new(5.0f, 3.0f, 1.0f, 3.0f),
            new(4.0f, 6.0f, 2.0f, 3.0f),
            new(3.0f, 5.0f, 1.0f, 4.0f),
            new(6.0f, 4.0f, 2.0f, 4.0f),
        };

        public bool IsActive() => Active.value;

        public override CustomPostProcessInjectionPoint injectionPoint =>
            CustomPostProcessInjectionPoint.BeforePostProcess;

        public override void Setup()
        {
            _shader = Shader.Find("Hidden/AlexMalyutin/EnhancedSMAA");
            _material = CoreUtils.CreateEngineMaterial(_shader);
            _props = new MaterialPropertyBlock();

            _system = new BufferedRTHandleSystem();
            _edgeTex = RTHandles.Alloc(
                Vector2.one,
                GraphicsFormat.R8G8B8A8_UNorm,
                wrapMode: TextureWrapMode.Clamp,
                filterMode: FilterMode.Point,
                useDynamicScale: true,
                dimension: TextureDimension.Tex2DArray, // NOTE: HDRP uses array to support XR.
                slices: TextureXR.slices,
                name: "SMAA_EdgeTex"
            );
            _blendTex = RTHandles.Alloc(
                Vector2.one,
                GraphicsFormat.R8G8B8A8_UNorm,
                wrapMode: TextureWrapMode.Clamp,
                filterMode: FilterMode.Point,
                useDynamicScale: true,
                dimension: TextureDimension.Tex2DArray, // NOTE: HDRP uses array to support XR.
                slices: TextureXR.slices,
                name: "SMAA_BlendTex"
            );
        }

        public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
        {
            if (!_material || !SMAAAreaTex.value || !SMAASearchTex.value) return;

            SetQualityLevel(_material, SMAAQuality.value);

            var width = destination.rt.width;
            var height = destination.rt.height;
            var smaaRTMetrics = new Vector4(1.0f / width, 1.0f / height, width, height);

            // Define: ENABLE_UPSCALER_FRAMEWORK
            if (SMAAMode.value is not EnhancedSMAAMode.SMAA_1x)
            {
                
            }

            var taaFrameIndex = camera.GetTaaFrameIndex();
            var subsampleIndexes = SMAAMode.value switch
            {
                EnhancedSMAAMode.SMAA_T2x or EnhancedSMAAMode.SMAA_S2x => _subsampleIndices2[taaFrameIndex % 2],
                EnhancedSMAAMode.SMAA_4x => _subsampleIndices4[taaFrameIndex % 4],
                _ => Vector4.zero,
            };

            // Edge Detection
            _props.SetTexture(_InputTexture, source);
            _props.SetVector(_SMAARTMetrics, smaaRTMetrics);
            ClearDrawFullScreen(cmd, _material, _edgeTex, _props, EdgeDetectionPassId);

            // Blend Weights Calculation
            _props.SetTexture(_InputTexture, _edgeTex);
            _props.SetTexture(_SMAAAreaTex, SMAAAreaTex.value);
            _props.SetTexture(_SMAASearchTex, SMAASearchTex.value);

            _props.SetVector("_SubsampleIndices", subsampleIndexes);
            ClearDrawFullScreen(cmd, _material, _blendTex, _props, BlendWeightsCalculationPassId);

            if (SMAAMode.value == EnhancedSMAAMode.SMAA_T2x)
            {
                if (camera.GetHistoryFrameCount(SMAAHistoryBufferId) == 0)
                {
                    camera.AllocHistoryFrameRT(SMAAHistoryBufferId, SMAAHistoryAllocator, 2);
                }

                if (_system.GetNumFramesAllocated(SMAAHistoryBufferId) == 0)
                {
                    _system.AllocBuffer(
                        SMAAHistoryBufferId,
                        static (system, frameIndex) => SMAAHistoryAllocator("Test", frameIndex, system),
                        2
                    );
                }

                var currentFrame = camera.GetCurrentFrameRT(SMAAHistoryBufferId);
                var previousFrame = camera.GetPreviousFrameRT(SMAAHistoryBufferId);

                var currentViewportSize = destination.rtHandleProperties.currentViewportSize;
                _system.SwapAndSetReferenceSize(currentViewportSize.x, currentViewportSize.y);
                currentFrame = _system.GetFrameRT(SMAAHistoryBufferId, 0);
                previousFrame = _system.GetFrameRT(SMAAHistoryBufferId, 1);

                // Neighborhood Blending
                _props.SetTexture(_InputTexture, source);
                _props.SetTexture(_SMAABlendTex, _blendTex);
                HDUtils.DrawFullScreen(cmd, _material, currentFrame, _props, NeighborhoodBlendingPassId);

                // Temporal Resolve
                // TODO: Use ping/pong _intermediateTex as history for next frame
                _props.SetTexture(_InputTexture, currentFrame);
                _props.SetTexture(_InputTexture2, previousFrame);
                HDUtils.DrawFullScreen(cmd, _material, destination, _props, TemporalResolvePassId);
            }
            else
            {
                // Neighborhood Blending
                _props.SetTexture(_InputTexture, source);
                _props.SetTexture(_SMAABlendTex, _blendTex);
                HDUtils.DrawFullScreen(cmd, _material, destination, _props, NeighborhoodBlendingPassId);
            }
        }

        public override void Cleanup()
        {
            CoreUtils.Destroy(_material);
            _edgeTex.Release();
            _blendTex.Release();
            _system.ReleaseAll();
        }

        private static void ClearDrawFullScreen(
            CommandBuffer commandBuffer,
            Material material,
            RTHandle colorBuffer,
            MaterialPropertyBlock properties,
            int shaderPassId
        )
        {
            CoreUtils.SetRenderTarget(commandBuffer, colorBuffer, ClearFlag.Color, Color.clear);
            commandBuffer.DrawProcedural(
                Matrix4x4.identity,
                material,
                shaderPassId,
                MeshTopology.Triangles,
                3,
                1,
                properties
            );
        }

        private static RTHandle SMAAHistoryAllocator(string id, int frameIndex, RTHandleSystem rtSystem)
        {
            // TODO: Match target format!
            //  hdPipeline.GetColorBufferFormat()
            frameIndex &= 1;
            return rtSystem.Alloc(Vector2.one, TextureXR.slices,
                colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
                dimension: TextureXR.dimension, enableRandomWrite: true,
                useDynamicScale: true,
                useDynamicScaleExplicit: true,
                name: $"{id}_SMAA_History{frameIndex}"
            );
        }

        private static void SetQualityLevel(Material material, EnhancedSMAAQualityLevel qualityLevel)
        {
            material.shaderKeywords = null;
            switch (qualityLevel)
            {
                case EnhancedSMAAQualityLevel.Low:
                    material.EnableKeyword("SMAA_PRESET_LOW");
                    break;
                case EnhancedSMAAQualityLevel.Medium:
                    material.EnableKeyword("SMAA_PRESET_MEDIUM");
                    break;
                case EnhancedSMAAQualityLevel.High:
                    material.EnableKeyword("SMAA_PRESET_HIGH");
                    break;
                case EnhancedSMAAQualityLevel.Ultra:
                    material.EnableKeyword("SMAA_PRESET_ULTRA");
                    break;
                default:
                    material.EnableKeyword("SMAA_PRESET_HIGH");
                    break;
            }
        }
    }
}
