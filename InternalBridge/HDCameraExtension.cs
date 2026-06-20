using UnityEngine.Rendering.HighDefinition;

namespace AlexMalyutin.EnhancedSMAA.InternalBridge
{
    public static class HDCameraExtension
    {
        public static int GetTaaFrameIndex(this HDCamera camera)
        {
            return camera.taaFrameIndex;
        }

        public static void SetCameraIUpscalerIsTemporalUpscaler(this HDCamera camera, bool active)
        {
#if ENABLE_UPSCALER_FRAMEWORK
            // TODO:
#endif
        }
    }
}
