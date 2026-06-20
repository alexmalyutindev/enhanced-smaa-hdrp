#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AlexMalyutin.EnhancedSMAA
{
    // ---------------------------------------------------------------------------
    // Options – passed in when the upscaler is registered/instantiated.
    // Expose these through your HDRP asset UI or a ScriptableObject as needed.
    // ---------------------------------------------------------------------------
    [Serializable]
    public class EnhancedSMAAOptions : UpscalerOptions
    {
        public Texture2D areaTexture;
        public Texture2D searchTexture;
        public EnhancedSMAAQualityLevel qualityLevel = EnhancedSMAAQualityLevel.High;
    }
}
#endif