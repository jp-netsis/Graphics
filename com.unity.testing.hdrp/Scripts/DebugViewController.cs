using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;

public class DebugViewController : MonoBehaviour
{
    public enum SettingType { Material, Lighting, Rendering }
    public SettingType settingType = SettingType.Material;

    [Header("Material")]
    [SerializeField] int gBuffer = 0;

    //DebugItemHandlerIntEnum(MaterialDebugSettings.debugViewMaterialGBufferStrings, MaterialDebugSettings.debugViewMaterialGBufferValues)
    [Header("Rendering")]
    [SerializeField] int fullScreenDebugMode = 0;

    [Header("Lighting")]
    [SerializeField] bool lightlayers = false;
    [SerializeField] int lightingFullScreenDebugMode = 0;
    [SerializeField] int lightingFullScreenDebugRTASView = 0;
    [SerializeField] int lightingFullScreenDebugRTASMode = 0;

    [ContextMenu("Set Debug View")]
    public void SetDebugView()
    {
        HDRenderPipeline hdPipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;

        switch (settingType)
        {
            case SettingType.Material:
                hdPipeline.debugDisplaySettings.SetDebugViewGBuffer(gBuffer);
                break;
            case SettingType.Lighting:
                hdPipeline.debugDisplaySettings.SetDebugLightLayersMode(lightlayers);
                hdPipeline.debugDisplaySettings.data.lightingDebugSettings.debugLightLayersFilterMask = (DebugLightLayersMask)0b10111101;
                hdPipeline.debugDisplaySettings.SetFullScreenDebugMode((FullScreenDebugMode)lightingFullScreenDebugMode);
                if ((FullScreenDebugMode)lightingFullScreenDebugMode == FullScreenDebugMode.RayTracingAccelerationStructure)
                {
                    hdPipeline.debugDisplaySettings.SetRTASDebugMode((RTASDebugMode)lightingFullScreenDebugRTASMode);
                    hdPipeline.debugDisplaySettings.SetRTASDebugView((RTASDebugView)lightingFullScreenDebugRTASView);
                }
                break;
            case SettingType.Rendering:
                hdPipeline.debugDisplaySettings.SetFullScreenDebugMode((FullScreenDebugMode)fullScreenDebugMode);
                break;
        }
    }

    void OnDestroy()
    {
        HDRenderPipeline hdPipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
        if (hdPipeline != null)
            ((IDebugData)hdPipeline.debugDisplaySettings).GetReset().Invoke();
    }
}
