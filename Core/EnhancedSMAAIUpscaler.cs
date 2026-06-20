#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.RenderGraphModule;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AlexMalyutin.EnhancedSMAA
{
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    static class RegisterEnhancedSMAA
    {
        static RegisterEnhancedSMAA() => Register();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InitRuntime() => Register();

        static void Register()
        {
            UpscalerRegistry.Register<EnhancedSMAAIUpscaler, EnhancedSMAAOptions>(
                EnhancedSMAAIUpscaler.UpscalerName
            );
        }
    }

    public class EnhancedSMAAIUpscaler : AbstractUpscaler, IDisposable
    {
        public const string UpscalerName = "EnhancedSMAA T2x (IUpscaler)";

        private const int PassEdgeDetection = 0;
        private const int PassBlendWeights = 1;
        private const int PassNeighborhoodBlending = 2;
        private const int PassTemporalResolve = 3;

        private static readonly Vector2[] KJitter =
        {
            new(0.25f, -0.25f),
            new(-0.25f, 0.25f),
        };

        private static readonly Vector4[] KSubsampleIndices =
        {
            new(1f, 1f, 1f, 0f),
            new(2f, 2f, 2f, 0f),
        };

        private readonly EnhancedSMAAOptions _options;
        private Material _material;
        private RTHandle _edgeTex;
        private RTHandle _blendTex;
        private readonly RTHandle[] _history = new RTHandle[2];
        private int _historyIndex;
        private bool _isFirstFrame = true;

        public EnhancedSMAAIUpscaler(EnhancedSMAAOptions options)
        {
            _options = options;
        }

        public override UpscalerOptions GetOptions() => _options;
        public override string GetName() => UpscalerName;
        public override bool IsTemporalUpscaler() => true;

        /// <summary>
        /// SMAA T2x uses a fixed 2-sample sub-pixel jitter, not the default
        /// 16-sample STP pattern. <paramref name="allowScaling"/> is false
        /// because T2x is full-resolution temporal AA, not an upscaler.
        /// </summary>
        public override void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            jitter = KJitter[frameIndex % 2];
            allowScaling = false;
        }

        /// <summary>
        /// SMAA T2x renders at full resolution – the pre-upscale resolution
        /// equals the post-upscale resolution.
        /// </summary>
        public override void NegotiatePreUpscaleResolution(
            ref Vector2Int preUpscaleResolution,
            Vector2Int postUpscaleResolution)
        {
            preUpscaleResolution = postUpscaleResolution;
        }

        public override bool IsSupportedXR() => false;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            EnsureInitialized();

            if (_material == null
                || _options.areaTexture == null
                || _options.searchTexture == null)
                return;

            var io = frameData.Get<UpscalingIO>();

            if (io.resetHistory)
                _isFirstFrame = true;

            int width = io.postUpscaleResolution.x;
            int height = io.postUpscaleResolution.y;
            var rtMetrics = new Vector4(1f / width, 1f / height, width, height);

            var subsampleIndexes = KSubsampleIndices[io.frameIndex & 1];

            int curr = _historyIndex, prev = 1 - curr;
            var currHistory = renderGraph.ImportTexture(_history[curr]);
            var prevHistory = _isFirstFrame ? currHistory : renderGraph.ImportTexture(_history[prev]);

            // TODO: Multiview support!
            var currView = io.viewMatrices[0];
            var currProj = io.projectionMatrices[0];

            Matrix4x4 reprojectionMatrix;
            if (_isFirstFrame)
            {
                reprojectionMatrix = Matrix4x4.identity;
            }
            else
            {
                var prevView = io.previousViewMatrices[0];
                var prevProj = io.previousProjectionMatrices[0];

                var currViewProj = currProj * currView;
                var prevViewProj = prevProj * prevView;
                reprojectionMatrix = prevViewProj * currViewProj.inverse;
            }

            var smaaData = new SMAAData()
            {
                CameraColor = io.cameraColor,
                CameraDepth = io.cameraDepth,
                CameraMotionVector = io.motionVectorColor,
                EdgeTexture = renderGraph.ImportTexture(_edgeTex),
                BlendTexture = renderGraph.ImportTexture(_blendTex),
                PrevProjection = reprojectionMatrix,
                CurrentHistory = currHistory,
                PrevHistory = prevHistory,
                RTMetrics = rtMetrics,
                SubsampleIndexes = subsampleIndexes,
            };

            RecordEdgeDetection(renderGraph, ref smaaData);
            RecordBlendWeights(renderGraph, ref smaaData);
            RecordNeighborhoodBlending(renderGraph, ref smaaData);
            RecordTemporalResolve(renderGraph, ref smaaData);

            _historyIndex = 1 - _historyIndex;
            _isFirstFrame = false;
        }

        private sealed class EdgeDetectionData
        {
            public Material Material;
            public TextureHandle CameraColor;
            public TextureHandle EdgeTex;
            public Vector4 RTMetrics;
        }

        private void RecordEdgeDetection(RenderGraph renderGraph, ref SMAAData smaaData)
        {
            using var builder = renderGraph.AddUnsafePass<EdgeDetectionData>(
                "SMAA – Edge Detection", out var passData);

            passData.Material = _material;
            passData.CameraColor = smaaData.CameraColor;
            builder.UseTexture(passData.CameraColor);
            passData.EdgeTex = smaaData.EdgeTexture;
            builder.UseTexture(passData.EdgeTex, AccessFlags.Write);
            passData.RTMetrics = smaaData.RTMetrics;

            builder.SetRenderFunc(static (EdgeDetectionData data, UnsafeGraphContext ctx) =>
            {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                CoreUtils.SetRenderTarget(cmd, data.EdgeTex, ClearFlag.Color, Color.clear);

                var props = new MaterialPropertyBlock();
                props.SetTexture(ShaderIDs.InputTex, data.CameraColor);
                props.SetVector(ShaderIDs.RTMetrics, data.RTMetrics);
                DrawFullScreenTriangle(cmd, data.Material, PassEdgeDetection, props);
            });
        }

        private sealed class BlendWeightsData
        {
            public Material Material;
            public TextureHandle EdgeTex;
            public TextureHandle BlendTex;

            public Texture AreaTex;
            public Texture SearchTex;

            public Vector4 RTMetrics;
            public Vector4 SubsampleIndexes;
        }

        private void RecordBlendWeights(RenderGraph renderGraph, ref SMAAData smaaData)
        {
            using var builder = renderGraph.AddUnsafePass<BlendWeightsData>(
                "SMAA – Blend Weights", out var passData);

            passData.Material = _material;
            passData.EdgeTex = smaaData.EdgeTexture;
            builder.UseTexture(passData.EdgeTex);
            passData.BlendTex = smaaData.BlendTexture;
            builder.UseTexture(passData.BlendTex, AccessFlags.Write);

            passData.AreaTex = _options.areaTexture;
            passData.SearchTex = _options.searchTexture;
            passData.RTMetrics = smaaData.RTMetrics;
            passData.SubsampleIndexes = smaaData.SubsampleIndexes;

            builder.SetRenderFunc(static (BlendWeightsData data, UnsafeGraphContext ctx) =>
            {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                CoreUtils.SetRenderTarget(cmd, data.BlendTex, ClearFlag.Color, Color.clear);

                var props = new MaterialPropertyBlock();
                props.SetTexture(ShaderIDs.InputTex, data.EdgeTex);
                props.SetTexture(ShaderIDs.AreaTex, data.AreaTex);
                props.SetTexture(ShaderIDs.SearchTex, data.SearchTex);
                props.SetVector(ShaderIDs.RTMetrics, data.RTMetrics);
                props.SetVector(ShaderIDs.SubsampleIdx, data.SubsampleIndexes);
                DrawFullScreenTriangle(cmd, data.Material, PassBlendWeights, props);
            });
        }

        private sealed class NeighborhoodBlendingData
        {
            public Material Material;
            public TextureHandle CameraColor;
            public TextureHandle CameraDepth;

            public TextureHandle BlendTex;
            public TextureHandle Output;

            public Vector4 RTMetrics;
            public Matrix4x4 PrevProjectionMatrix;
        }

        private void RecordNeighborhoodBlending(RenderGraph renderGraph, ref SMAAData smaaData)
        {
            using var builder = renderGraph.AddUnsafePass<NeighborhoodBlendingData>(
                "SMAA – Neighborhood Blending", out var passData);

            passData.Material = _material;
            passData.RTMetrics = smaaData.RTMetrics;
            passData.PrevProjectionMatrix = smaaData.PrevProjection;

            passData.CameraDepth = smaaData.CameraDepth;
            builder.UseTexture(passData.CameraDepth);
            passData.CameraColor = smaaData.CameraColor;
            builder.UseTexture(passData.CameraColor);
            passData.BlendTex = smaaData.BlendTexture;
            builder.UseTexture(passData.BlendTex);
            passData.Output = smaaData.CurrentHistory;
            builder.UseTexture(passData.Output, AccessFlags.Write);

            builder.SetRenderFunc(static (NeighborhoodBlendingData data, UnsafeGraphContext ctx) =>
            {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                var props = new MaterialPropertyBlock();
                props.SetTexture(ShaderIDs.InputTex, data.CameraColor);
                props.SetTexture(ShaderIDs.CameraDepthTexture, data.CameraDepth);
                props.SetTexture(ShaderIDs.BlendTex, data.BlendTex);
                props.SetVector(ShaderIDs.RTMetrics, data.RTMetrics);
                props.SetMatrix(ShaderIDs.ReprojectionMatrix, data.PrevProjectionMatrix);
                HDUtils.DrawFullScreen(cmd, data.Material, data.Output, props, PassNeighborhoodBlending);
            });
        }

        private sealed class TemporalResolveData
        {
            public Material Material;
            public TextureHandle CameraDepth;
            public TextureHandle CurrentHistory;
            public TextureHandle PreviousHistory;
            public TextureHandle Output;
            public Vector4 RTMetrics;
            public Matrix4x4 PrevProjectionMatrix;
        }

        private void RecordTemporalResolve(RenderGraph renderGraph, ref SMAAData smaaData)
        {
            using var builder = renderGraph.AddUnsafePass<TemporalResolveData>(
                "SMAA – Temporal Resolve", out var passData);

            passData.Material = _material;
            passData.RTMetrics = smaaData.RTMetrics;
            passData.PrevProjectionMatrix = smaaData.PrevProjection;

            passData.CameraDepth = smaaData.CameraDepth;
            builder.UseTexture(passData.CameraDepth);
            passData.CurrentHistory = smaaData.CurrentHistory;
            builder.UseTexture(passData.CurrentHistory);
            passData.PreviousHistory = smaaData.PrevHistory;
            builder.UseTexture(passData.PreviousHistory);
            passData.Output = smaaData.CameraColor;
            builder.UseTexture(passData.Output, AccessFlags.Write);

            builder.SetRenderFunc(static (TemporalResolveData data, UnsafeGraphContext ctx) =>
            {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                var props = new MaterialPropertyBlock();
                props.SetTexture(ShaderIDs.CameraDepthTexture, data.CameraDepth);
                props.SetTexture(ShaderIDs.InputTex, data.CurrentHistory);
                props.SetTexture(ShaderIDs.InputTex2, data.PreviousHistory);
                props.SetVector(ShaderIDs.RTMetrics, data.RTMetrics);
                props.SetMatrix(ShaderIDs.ReprojectionMatrix, data.PrevProjectionMatrix);
                HDUtils.DrawFullScreen(cmd, data.Material, data.Output, props, PassTemporalResolve);
            });
        }

        private void EnsureInitialized()
        {
            if (_material != null) return;

            var shader = Shader.Find("Hidden/AlexMalyutin/EnhancedSMAA");
            _material = CoreUtils.CreateEngineMaterial(shader);
            SetQualityLevel(_material, _options.qualityLevel);

            _edgeTex = RTHandles.Alloc(
                Vector2.one,
                colorFormat: GraphicsFormat.R8G8B8A8_UNorm,
                wrapMode: TextureWrapMode.Clamp,
                filterMode: FilterMode.Bilinear,
                useDynamicScale: true,
                dimension: TextureDimension.Tex2DArray,
                slices: TextureXR.slices,
                name: "SMAA_Up_EdgeTex"
            );
            _blendTex = RTHandles.Alloc(
                Vector2.one,
                colorFormat: GraphicsFormat.R8G8B8A8_UNorm,
                wrapMode: TextureWrapMode.Clamp,
                filterMode: FilterMode.Bilinear,
                useDynamicScale: true,
                dimension: TextureDimension.Tex2DArray,
                slices: TextureXR.slices,
                name: "SMAA_Up_BlendTex"
            );

            for (int i = 0; i < 2; i++)
            {
                _history[i] = RTHandles.Alloc(
                    Vector2.one,
                    slices: TextureXR.slices,
                    colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
                    dimension: TextureXR.dimension,
                    enableRandomWrite: true,
                    useDynamicScale: true,
                    useDynamicScaleExplicit: true,
                    name: $"SMAA_Up_History{i}"
                );
            }
        }

        // AbstractUpscaler does not implement IDisposable, so we add it here.
        // Call Dispose() wherever the upscaler lifetime ends (camera destroyed,
        // upscaler unregistered, etc.).
        public void Dispose()
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _edgeTex?.Release();
            _edgeTex = null;
            _blendTex?.Release();
            _blendTex = null;
            for (int i = 0; i < 2; i++)
            {
                _history[i]?.Release();
                _history[i] = null;
            }

            _isFirstFrame = true;
        }

        private static void SetQualityLevel(Material mat, EnhancedSMAAQualityLevel level)
        {
            mat.shaderKeywords = null;
            mat.EnableKeyword(level switch
            {
                EnhancedSMAAQualityLevel.Low => "SMAA_PRESET_LOW",
                EnhancedSMAAQualityLevel.Medium => "SMAA_PRESET_MEDIUM",
                EnhancedSMAAQualityLevel.High => "SMAA_PRESET_HIGH",
                EnhancedSMAAQualityLevel.Ultra => "SMAA_PRESET_ULTRA",
                _ => "SMAA_PRESET_HIGH",
            });
            // TODO: SMAA_REPROJECTION with velocity vectors.
            mat.EnableKeyword("SMAA_UV_BASED_REPROJECTION");
        }

        private static void DrawFullScreenTriangle(
            CommandBuffer cmd,
            Material material,
            int passIndex,
            MaterialPropertyBlock props)
        {
            cmd.DrawProcedural(
                Matrix4x4.identity,
                material,
                passIndex,
                MeshTopology.Triangles,
                vertexCount: 3,
                instanceCount: 1,
                props
            );
        }

        private static Matrix4x4 ApplyJitter(Matrix4x4 nonJitteredProj, Vector2 jitterPixels, int width, int height)
        {
            var planes = nonJitteredProj.decomposeProjection;
            float vertFov = Mathf.Abs(planes.top) + Mathf.Abs(planes.bottom);
            float horizFov = Mathf.Abs(planes.left) + Mathf.Abs(planes.right);
            var planeJitter = new Vector2(jitterPixels.x * horizFov / width, jitterPixels.y * vertFov / height);
            planes.left += planeJitter.x;
            planes.right += planeJitter.x;
            planes.top  += planeJitter.y;
            planes.bottom += planeJitter.y;
            return Matrix4x4.Frustum(planes);
        }

        /// <summary>
        /// Re-implementation of HDCamera.GetJitteredProjectionMatrix's perspective branch.
        /// Applies a pixel-space jitter offset to a projection matrix by shifting the
        /// near-plane frustum bounds, matching HDRP's own jitter application exactly.
        /// </summary>
        /// <param name="origProj">The unjittered projection matrix (e.g. nonJitteredProjMatrix / prevProjMatrix).</param>
        /// <param name="jitterPixels">
        /// The ACTUAL jitter offset in pixels, in the same convention HDCamera itself uses
        /// internally (i.e. post-negation, post-taaJitterScale — this is jitterX/jitterY
        /// exactly as they appear right before HDCamera builds taaJitter).
        /// </param>
        /// <param name="width">actualWidth for the frame this projection matrix belongs to.</param>
        /// <param name="height">actualHeight for the frame this projection matrix belongs to.</param>
        /// <param name="zFarFallback">
        /// Substitute for HDCamera's frustum.planes[5].distance, used only when the
        /// decomposed projection's zFar evaluates to infinity (very large far clip planes).
        /// Pass camera.farClipPlane if you don't have anything better.
        /// </param>
        private static Matrix4x4 ApplyJitterToProjection(
            Matrix4x4 origProj,
            Vector2 jitterPixels,
            int width,
            int height,
            float zFarFallback)
        {
            var planes = origProj.decomposeProjection;

            float vertFov = Mathf.Abs(planes.top) + Mathf.Abs(planes.bottom);
            float horizFov = Mathf.Abs(planes.left) + Mathf.Abs(planes.right);

            var planeJitter = new Vector2(
                jitterPixels.x * horizFov / width,
                jitterPixels.y * vertFov / height);

            planes.left += planeJitter.x;
            planes.right += planeJitter.x;
            planes.top += planeJitter.y;
            planes.bottom += planeJitter.y;

            // Reconstruct the far plane for the jittered matrix.
            // For extremely high far clip planes, the decomposed projection zFar evaluates to infinity.
            if (float.IsInfinity(planes.zFar))
                planes.zFar = zFarFallback;

            return Matrix4x4.Frustum(planes);
        }

        private struct SMAAData
        {
            public TextureHandle CameraColor;
            public TextureHandle CameraDepth;
            public TextureHandle CameraMotionVector;

            public TextureHandle EdgeTexture;
            public TextureHandle BlendTexture;

            public Vector4 RTMetrics;
            public Vector4 SubsampleIndexes;
            public Matrix4x4 PrevProjection;
            public TextureHandle CurrentHistory;
            public TextureHandle PrevHistory;
        }
    }

    public class ShaderIDs
    {
        public static readonly int InputTex = Shader.PropertyToID("_InputTexture");
        public static readonly int InputTex2 = Shader.PropertyToID("_InputTexture2");
        public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");

        public static readonly int AreaTex = Shader.PropertyToID("_AreaTex");
        public static readonly int SearchTex = Shader.PropertyToID("_SearchTex");
        public static readonly int BlendTex = Shader.PropertyToID("_BlendTex");
        public static readonly int RTMetrics = Shader.PropertyToID("_SMAARTMetrics");
        public static readonly int SubsampleIdx = Shader.PropertyToID("_SubsampleIndices");
        public static readonly int ReprojectionMatrix = Shader.PropertyToID("_ReprojectionMatrix");
    }
}
#endif
