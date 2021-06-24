using System.Collections.Generic;
using UnityEngine.VFX;
using System;
using System.Diagnostics;
using System.Linq;
using UnityEngine.Experimental.GlobalIllumination;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        internal struct VBufferOutput
        {
            public TextureHandle vBuffer0;
            public TextureHandle depthBuffer;
        }

        class VBufferPassData
        {
            public uint clusterBackCount;
            public uint clusterFrontCount;
            public uint clusterDoubleCount;
            public Material renderVisibilityMaterial;
            public TextureHandle tempColorBuffer;
            public TextureHandle vbuffer0;
            public TextureHandle depthBuffer;
            public FrameSettings frameSettings;
        }

        VBufferOutput RenderVBuffer(RenderGraph renderGraph, CullingResults cullingResults, HDCamera hdCamera, TextureHandle tempColorBuffer)
        {
            VBufferOutput vBufferOutput = new VBufferOutput();
            if (InstanceVDataB == null || CompactedVB == null || CompactedIB == null) return vBufferOutput;

            // These flags are still required in SRP or the engine won't compute previous model matrices...
            // If the flag hasn't been set yet on this camera, motion vectors will skip a frame.
            hdCamera.camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;

            using (var builder = renderGraph.AddRenderPass<VBufferPassData>("VBuffer Prepass", out var passData, ProfilingSampler.Get(HDProfileId.VBufferPrepass)))
            {
                builder.AllowRendererListCulling(false);

                passData.clusterBackCount = instanceCountBack;
                passData.clusterFrontCount = instanceCountFront;
                passData.clusterDoubleCount = instanceCountDouble;
                passData.renderVisibilityMaterial = m_VisibilityBufferMaterial;
                passData.tempColorBuffer = builder.WriteTexture(tempColorBuffer);
                passData.vbuffer0 = builder.WriteTexture(renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                    { colorFormat = GraphicsFormat.R32_UInt, clearBuffer = true, enableRandomWrite = true, name = "VBuffer 0" }));

                passData.depthBuffer = CreateDepthBuffer(renderGraph, true, hdCamera.msaaSamples);

                builder.UseDepthBuffer(passData.depthBuffer, DepthAccess.ReadWrite);
                builder.UseColorBuffer(passData.vbuffer0, 0);

                passData.frameSettings = hdCamera.frameSettings;

                builder.SetRenderFunc(
                    (VBufferPassData data, RenderGraphContext context) =>
                    {
                        context.cmd.SetGlobalBuffer("_CompactedVertexBuffer", CompactedVB);
                        context.cmd.SetGlobalBuffer("_CompactedIndexBuffer", CompactedIB);
                        context.cmd.SetGlobalBuffer("_InstanceVDataBuffer", InstanceVDataB);

                        context.cmd.SetGlobalInt("_InstanceVDataShift", 0);
                        context.cmd.DrawProcedural(Matrix4x4.identity, data.renderVisibilityMaterial, 0, MeshTopology.Triangles, VisibilityBufferConstants.s_ClusterSizeInIndices, (int)data.clusterBackCount);
                        context.cmd.SetGlobalInt("_InstanceVDataShift", (int)data.clusterBackCount);
                        if (data.clusterFrontCount > 0)
                            context.cmd.DrawProcedural(Matrix4x4.identity, data.renderVisibilityMaterial, 1, MeshTopology.Triangles, VisibilityBufferConstants.s_ClusterSizeInIndices, (int)data.clusterFrontCount);
                        context.cmd.SetGlobalInt("_InstanceVDataShift", (int)data.clusterBackCount + (int)data.clusterFrontCount);
                        if (data.clusterDoubleCount > 0)
                            context.cmd.DrawProcedural(Matrix4x4.identity, data.renderVisibilityMaterial, 2, MeshTopology.Triangles, VisibilityBufferConstants.s_ClusterSizeInIndices, (int)data.clusterDoubleCount);
                    });

                vBufferOutput.vBuffer0 = passData.vbuffer0;
                vBufferOutput.depthBuffer = passData.depthBuffer;

                PushFullScreenDebugTexture(renderGraph, vBufferOutput.vBuffer0, FullScreenDebugMode.VBufferTriangleId, GraphicsFormat.R32_UInt);
                PushFullScreenDebugTexture(renderGraph, vBufferOutput.vBuffer0, FullScreenDebugMode.VBufferGeometryId, GraphicsFormat.R32_UInt);
            }
            return vBufferOutput;
        }

        class VBufferLightingPassData
        {
            public int width;
            public int height;
            public TextureHandle colorBuffer;
            public TextureHandle vbuffer0;
            public TextureHandle materialDepthBuffer;
            public TextureHandle cameraDepthTexture;
            public ComputeBufferHandle vertexBuffer;
            public ComputeBufferHandle indexBuffer;
            public ComputeBufferHandle instancedDataBuffer;
            public ComputeBufferHandle lightListBuffer;
            public TextureHandle vbufferTileClassification;
            public TextureHandle materialTile;
            public TextureHandle bucketID;
        }

        [GenerateHLSL]
        internal enum MaterialVariants
        {
            SkyDirEnv = LightFeatureFlags.Sky | LightFeatureFlags.Directional | LightFeatureFlags.Env | LightFeatureFlags.ProbeVolume,
            SkyDirPunctualEnv = LightFeatureFlags.Sky | LightFeatureFlags.Directional | LightFeatureFlags.Punctual | LightFeatureFlags.Env | LightFeatureFlags.ProbeVolume,
            SkyDirPunctualAreaEnv = LightFeatureFlags.Sky | LightFeatureFlags.Directional | LightFeatureFlags.Punctual | LightFeatureFlags.Area | LightFeatureFlags.Env | LightFeatureFlags.ProbeVolume
        }

        TextureHandle RenderVBufferLighting(RenderGraph renderGraph, CullingResults cullingResults, HDCamera hdCamera,
            VBufferOutput vBufferOutput, TextureHandle materialDepthBuffer,
            TextureHandle colorBuffer,
            TextureHandle vbufferTileClassification, TextureHandle materialTile, TextureHandle bucketID, 
            in BuildGPULightListOutput lightLists)
        {
            if (InstanceVDataB == null || CompactedVB == null || CompactedIB == null) return colorBuffer;

            using (var builder = renderGraph.AddRenderPass<VBufferLightingPassData>("VBuffer Lighting", out var passData, ProfilingSampler.Get(HDProfileId.VBufferLighting)))
            {
                builder.AllowRendererListCulling(false);

                passData.width = hdCamera.actualWidth;
                passData.height = hdCamera.actualHeight;
                passData.colorBuffer = builder.UseColorBuffer(colorBuffer, 0);
                passData.vbuffer0 = builder.ReadTexture(vBufferOutput.vBuffer0);
                passData.cameraDepthTexture = builder.ReadTexture(vBufferOutput.depthBuffer);
                passData.materialDepthBuffer = builder.UseDepthBuffer(materialDepthBuffer, DepthAccess.ReadWrite);
                passData.vertexBuffer = renderGraph.ImportComputeBuffer(CompactedVB);
                passData.indexBuffer = renderGraph.ImportComputeBuffer(CompactedIB);
                passData.instancedDataBuffer = renderGraph.ImportComputeBuffer(InstanceVDataB);
                passData.lightListBuffer = builder.ReadComputeBuffer(lightLists.lightList);
                passData.vbufferTileClassification = builder.ReadTexture(vbufferTileClassification);
                passData.materialTile = builder.ReadTexture(materialTile);
                passData.bucketID = builder.ReadTexture(bucketID);

                builder.SetRenderFunc(
                    (VBufferLightingPassData data, RenderGraphContext context) =>
                    {
                        context.cmd.SetGlobalTexture("_VBufferTileClassification", data.vbufferTileClassification);
                        context.cmd.SetGlobalBuffer("_CompactedVertexBuffer", data.vertexBuffer);
                        context.cmd.SetGlobalBuffer("_CompactedIndexBuffer", data.indexBuffer);
                        context.cmd.SetGlobalBuffer("_InstanceVDataBuffer", data.instancedDataBuffer);
                        context.cmd.SetGlobalTexture("_VBuffer0", data.vbuffer0);
                        context.cmd.SetGlobalTexture("_VBufferDepthTexture", data.cameraDepthTexture);
                        context.cmd.SetGlobalTexture("_ClassificationTileInput", data.materialTile);
                        context.cmd.SetGlobalTexture("_BucketTileInput", data.bucketID);

                        context.cmd.SetGlobalBuffer(HDShaderIDs.g_vLightListGlobal, data.lightListBuffer);

                        var materialList = materials.Keys.ToArray();
                        for (int matIdx = 0; matIdx < materialList.Length; ++matIdx)
                        {
                            var material = materialList[matIdx];
                            if (IsTransparentMaterial(material) || IsAlphaTestedMaterial(material))
                                continue;

                            var passIdx = -1;
                            for (int i = 0; i < material.passCount; ++i)
                            {
                                if (material.GetPassName(i).IndexOf("VBufferLighting") >= 0)
                                {
                                    passIdx = i;
                                    break;
                                }
                            }

                            if (passIdx == -1) continue;

                            int quadTileSize = 64;
                            int numTileX = HDUtils.DivRoundUp(data.width, quadTileSize);
                            int numTileY = HDUtils.DivRoundUp(data.height, quadTileSize);

                            context.cmd.SetGlobalInt("_BucketID", materials[material].bucketID);
                            context.cmd.SetGlobalInt("_CurrMaterialID", materials[material].globalMaterialID);
                            context.cmd.SetGlobalVector("_VBufferTileData", new Vector4((float)numTileX, (float)numTileY, (float)quadTileSize, 0.0f));
                            context.cmd.SetViewport(new Rect(0, 0, numTileX * quadTileSize, numTileY * quadTileSize));


                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_ENV", false);
                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_PUNCTUAL_ENV", false);
                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_PUNCTUAL_AREA_ENV", true);
                            context.cmd.DrawProcedural(Matrix4x4.identity, material, passIdx, MeshTopology.Triangles, 6, numTileX * numTileY);

                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_PUNCTUAL_ENV", false);
                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_PUNCTUAL_AREA_ENV", false);
                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_ENV", true);
                            context.cmd.DrawProcedural(Matrix4x4.identity, material, passIdx, MeshTopology.Triangles, 6, numTileX * numTileY);

                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_ENV", false);
                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_PUNCTUAL_AREA_ENV", false);
                            CoreUtils.SetKeyword(context.cmd, "VARIANT_DIR_PUNCTUAL_ENV", true);
                            context.cmd.DrawProcedural(Matrix4x4.identity, material, passIdx, MeshTopology.Triangles, 6, numTileX * numTileY);
                        }
                    });

                PushFullScreenDebugTexture(renderGraph, colorBuffer, FullScreenDebugMode.VBufferLightingDebug);
            }
            return colorBuffer;
        }

        class VBufferMaterialDepthPassData
        {
            public TextureHandle outputDepthBuffer;
            public TextureHandle dummyColorOutput;
            public Material createMaterialDepthMaterial;
        }

        TextureHandle RenderMaterialDepth(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle colorBuffer)
        {
            var outputDepth = CreateDepthBuffer(renderGraph, true, hdCamera.msaaSamples);
            using (var builder = renderGraph.AddRenderPass<VBufferMaterialDepthPassData>("Create Vis Buffer Material Depth", out var passData, ProfilingSampler.Get(HDProfileId.VBufferMaterialDepth)))
            {
                passData.outputDepthBuffer = outputDepth;
                passData.createMaterialDepthMaterial = m_CreateMaterialDepthMaterial;
                passData.dummyColorOutput = builder.WriteTexture(colorBuffer);
                builder.UseDepthBuffer(passData.outputDepthBuffer, DepthAccess.ReadWrite);

                builder.SetRenderFunc(
                    (VBufferMaterialDepthPassData data, RenderGraphContext context) =>
                    {
                        // Doesn't matter what's bound as color buffer
                        HDUtils.DrawFullScreen(context.cmd, passData.createMaterialDepthMaterial, passData.dummyColorOutput, passData.outputDepthBuffer, null, 0);
                    });

                PushFullScreenDebugTexture(renderGraph, outputDepth, FullScreenDebugMode.VBufferMaterialId, GraphicsFormat.R32_SFloat);
            }

            return outputDepth;
        }

        class VBufferTileClassficationData
        {
            public int tileClassSizeX;
            public int tileClassSizeY;
            public ComputeShader createTileClassification;
            public TextureHandle outputTile;
            public ComputeBufferHandle tileFeatureFlagsBuffer;
        }

        TextureHandle VBufferTileClassification(RenderGraph renderGraph, HDCamera hdCamera, ComputeBufferHandle tileFeatureFlags, TextureHandle colorBuffer)
        {
            int tileClassSizeX = HDUtils.DivRoundUp(hdCamera.actualWidth, 64);
            int tileClassSizeY = HDUtils.DivRoundUp(hdCamera.actualHeight, 64);

            var tileClassification = renderGraph.CreateTexture(new TextureDesc(tileClassSizeX, tileClassSizeY, true, true)
                { colorFormat = GraphicsFormat.R32G32_UInt, clearBuffer = true, enableRandomWrite = true, name = "Tile classification" });
            using (var builder = renderGraph.AddRenderPass<VBufferTileClassficationData>("Create VBuffer Tiles", out var passData, ProfilingSampler.Get(HDProfileId.VBufferLightTileClassification)))
            {
                passData.outputTile = builder.WriteTexture(tileClassification);

                passData.tileClassSizeX = tileClassSizeX;
                passData.tileClassSizeY = tileClassSizeY;
                passData.createTileClassification = defaultResources.shaders.classificationTilesCS;
                passData.tileFeatureFlagsBuffer = builder.ReadComputeBuffer(tileFeatureFlags);

                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (VBufferTileClassficationData data, RenderGraphContext context) =>
                    {
                        var cs = data.createTileClassification;
                        var kernel = cs.FindKernel("CreateVisibilityBuffClassification");

                        context.cmd.SetComputeBufferParam(cs, kernel, HDShaderIDs.g_TileFeatureFlags, data.tileFeatureFlagsBuffer);
                        context.cmd.SetComputeTextureParam(cs, kernel, "_ClassificationTile", data.outputTile);



                        int dispatchX = HDUtils.DivRoundUp(data.tileClassSizeX, 8);
                        int dispatchY = HDUtils.DivRoundUp(data.tileClassSizeY, 8);

                        context.cmd.SetComputeVectorParam(cs, "_TileBufferSize", new Vector4(data.tileClassSizeX, data.tileClassSizeY, 0, 0));

                        context.cmd.DispatchCompute(cs, kernel, dispatchX, dispatchY, 1);
                    });
            }

            return tileClassification;
        }

        class VBufferMaterialTileClassficationData
        {
            public int tileClassSizeX;
            public int tileClassSizeY;
            public ComputeShader createMaterialTile;
            public TextureHandle outputTile;
            public TextureHandle outputBucketTile;
            public TextureHandle tile8x;
            public TextureHandle bucketTile8x;
            public TextureHandle vBuffer0;
            public int actualWidth;
            public int actualHeight;
            public ComputeBufferHandle instancedDataBuffer;
        }

        void VBufferMaterialTile(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle vBuffer0, out TextureHandle tileClassification, out TextureHandle bucketID)
        {
            int tileClassSizeX = HDUtils.DivRoundUp(hdCamera.actualWidth, 64);
            int tileClassSizeY = HDUtils.DivRoundUp(hdCamera.actualHeight, 64);

            tileClassification = renderGraph.CreateTexture(new TextureDesc(tileClassSizeX, tileClassSizeY, true, true)
                { colorFormat = GraphicsFormat.R16G16_UInt, clearBuffer = true, enableRandomWrite = true, name = "Material Tile classification" });
            bucketID = renderGraph.CreateTexture(new TextureDesc(tileClassSizeX, tileClassSizeY, true, true)
                { colorFormat = GraphicsFormat.R8_UInt, clearBuffer = true, enableRandomWrite = true, name = "Bucket ID" });

            using (var builder = renderGraph.AddRenderPass<VBufferMaterialTileClassficationData>("Create Material Tile", out var passData, ProfilingSampler.Get(HDProfileId.VBufferMaterialTileClassification)))
            {
                builder.AllowPassCulling(false);

                int tileClassSizeIntermediateX = HDUtils.DivRoundUp(hdCamera.actualWidth, 8);
                int tileClassSizeIntermediateY = HDUtils.DivRoundUp(hdCamera.actualHeight, 8);
                passData.tile8x = builder.CreateTransientTexture(new TextureDesc(tileClassSizeIntermediateX, tileClassSizeIntermediateY, true, true)
                    { colorFormat = GraphicsFormat.R16G16_UInt, enableRandomWrite = true, name = "Material mask 8x" });
                passData.bucketTile8x = builder.CreateTransientTexture(new TextureDesc(tileClassSizeIntermediateX, tileClassSizeIntermediateY, true, true)
                    { colorFormat = GraphicsFormat.R8_UInt, enableRandomWrite = true, name = "Bucket mask 8x" });

                passData.tileClassSizeX = tileClassSizeX;
                passData.tileClassSizeY = tileClassSizeY;
                passData.vBuffer0 = builder.ReadTexture(vBuffer0);
                passData.outputTile = builder.WriteTexture(tileClassification);
                passData.outputBucketTile = builder.WriteTexture(bucketID);

                passData.createMaterialTile = defaultResources.shaders.materialTileClassificationCS;
                passData.instancedDataBuffer = renderGraph.ImportComputeBuffer(InstanceVDataB);

                passData.actualWidth = hdCamera.actualWidth;
                passData.actualHeight = hdCamera.actualHeight;

                builder.SetRenderFunc(
                    (VBufferMaterialTileClassficationData data, RenderGraphContext context) =>
                    {
                        var cs = data.createMaterialTile;
                        var kernel = cs.FindKernel("MaterialReduction");

                        context.cmd.SetComputeBufferParam(cs, kernel, "_InstanceVDataBuffer", data.instancedDataBuffer);
                        context.cmd.SetComputeTextureParam(cs, kernel, "_VBuffer0", data.vBuffer0);
                        context.cmd.SetComputeTextureParam(cs, kernel, "_ClassificationTile", data.tile8x);
                        context.cmd.SetComputeTextureParam(cs, kernel, "_BucketTile", data.bucketTile8x);

                        int dispatchX = HDUtils.DivRoundUp(data.actualWidth, 8);
                        int dispatchY = HDUtils.DivRoundUp(data.actualHeight, 8);

                        context.cmd.DispatchCompute(cs, kernel, dispatchX, dispatchY, 1);

                        kernel = cs.FindKernel("FinalReduction");
                        context.cmd.SetComputeTextureParam(cs, kernel, "_ClassificationTileInput", data.tile8x);
                        context.cmd.SetComputeTextureParam(cs, kernel, "_BucketTileInput", data.bucketTile8x);
                        context.cmd.SetComputeTextureParam(cs, kernel, "_ClassificationTile", data.outputTile);
                        context.cmd.SetComputeTextureParam(cs, kernel, "_BucketTile", data.outputBucketTile);

                        dispatchX = HDUtils.DivRoundUp(dispatchX, 8);
                        dispatchY = HDUtils.DivRoundUp(dispatchY, 8);

                        context.cmd.DispatchCompute(cs, kernel, dispatchX, dispatchY, 1);
                    });
            }
        }
    }
}
