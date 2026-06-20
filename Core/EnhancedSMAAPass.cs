using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;

namespace AlexMalyutin.EnhancedSMAA
{
    [Serializable]
    public class EnhancedSMAAPass : CustomPass
    {
        [Header("Input")]
        public Shader SMAA;
        public Texture AreaTex;
        public Texture SearchTex;

        [Header("Settings")] 
        public EnhancedSMAAQualityLevel Quality = EnhancedSMAAQualityLevel.Medium;
        private EnhancedSMAAQualityLevel _activeQuality = EnhancedSMAAQualityLevel.Medium;

        private MaterialPropertyBlock _props;
        private RTHandle _colorTex;
        private RTHandle _edgeTex;
        private RTHandle _blendTex;
        private Material _material;

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (SMAA != null)
            {
                _material = CoreUtils.CreateEngineMaterial(SMAA);
                UpdatePreset(true);
            }

            _props = new MaterialPropertyBlock();
            _colorTex = RTHandles.Alloc(
                Vector2.one,
                GraphicsFormat.B10G11R11_UFloatPack32,
                wrapMode: TextureWrapMode.Clamp,
                filterMode: FilterMode.Point,
                useDynamicScale: true,
                name: "SMAA_ColorTex"
            );
            _edgeTex = RTHandles.Alloc(
                Vector2.one,
                GraphicsFormatUtility.GetGraphicsFormat(RenderTextureFormat.RG16, false),
                wrapMode: TextureWrapMode.Clamp,
                filterMode: FilterMode.Point,
                useDynamicScale: true,
                name: "SMAA_EdgeTex"
            );
            _blendTex = RTHandles.Alloc(
                Vector2.one,
                GraphicsFormatUtility.GetGraphicsFormat(RenderTextureFormat.ARGB32, false),
                wrapMode: TextureWrapMode.Clamp,
                filterMode: FilterMode.Point,
                useDynamicScale: true,
                name: "SMAA_BlendTex"
            );
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (_material == null || AreaTex == null || SearchTex == null) return;

            UpdatePreset();

            var inputColorTex = ctx.cameraColorBuffer;
            var width = inputColorTex.rt.width;
            var height = inputColorTex.rt.width;

            _props.Clear();
            _props.SetVector("_InputTexelSize", new Vector4(1.0f / width, 1.0f / height, width, height));

            var cmd = ctx.cmd;

            cmd.CopyTexture(
                inputColorTex, 0, 0,
                _colorTex, 0, 0
            );

            // EdgeDetection
            CoreUtils.SetRenderTarget(cmd, _edgeTex, ClearFlag.Color, Color.clear);
            _props.SetTexture("_ColorTex", _colorTex);
            HDUtils.DrawFullScreen(cmd, _material, _edgeTex, _props, shaderPassId: 0);

            // BlendingWeights
            CoreUtils.SetRenderTarget(cmd, _blendTex, ClearFlag.Color, Color.clear);
            _props.SetTexture("_EdgeTex", _edgeTex);
            _props.SetTexture("_AreaTex", AreaTex);
            _props.SetTexture("_SearchTex", SearchTex);
            HDUtils.DrawFullScreen(cmd, _material, _blendTex, _props, shaderPassId: 1);

            // NeighborhoodBlend
            _props.SetTexture("_ColorTex", _colorTex);
            _props.SetTexture("_BlendTex", _blendTex);
            HDUtils.DrawFullScreen(cmd, _material, inputColorTex, _props, shaderPassId: 2);
        }

        private void UpdatePreset(bool force = false)
        {
            if (force || Quality != _activeQuality)
            {
                switch (_activeQuality)
                {
                    case EnhancedSMAAQualityLevel.Low: _material.DisableKeyword("SMAA_PRESET_LOW"); break;
                    case EnhancedSMAAQualityLevel.Medium: _material.DisableKeyword("SMAA_PRESET_MEDIUM"); break;
                    case EnhancedSMAAQualityLevel.High: _material.DisableKeyword("SMAA_PRESET_HIGH"); break;
                    case EnhancedSMAAQualityLevel.Ultra: _material.DisableKeyword("SMAA_PRESET_ULTRA"); break;
                    default: _material.DisableKeyword("SMAA_PRESET_LOW"); break;
                }

                switch (Quality)
                {
                    case EnhancedSMAAQualityLevel.Low: _material.EnableKeyword("SMAA_PRESET_LOW"); break;
                    case EnhancedSMAAQualityLevel.Medium: _material.EnableKeyword("SMAA_PRESET_MEDIUM"); break;
                    case EnhancedSMAAQualityLevel.High: _material.EnableKeyword("SMAA_PRESET_HIGH"); break;
                    case EnhancedSMAAQualityLevel.Ultra: _material.EnableKeyword("SMAA_PRESET_ULTRA"); break;
                    default: _material.EnableKeyword("SMAA_PRESET_LOW"); break;
                }

                _activeQuality = Quality;
            }
        }

        protected override void Cleanup()
        {
            _colorTex.Release();
            _edgeTex.Release();
            _blendTex.Release();

            CoreUtils.Destroy(_material);
        }
    }
}
