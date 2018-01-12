﻿using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    [ExecuteInEditMode]
    public class PlanarReflectionProbe : MonoBehaviour
    {
        [SerializeField]
        ProxyVolumeComponent m_ProxyVolumeReference;
        [SerializeField]
        InfluenceVolume m_InfluenceVolume;
        [SerializeField]
        Vector3 m_CaptureLocalPosition;
        [SerializeField]
        [Range(0, 1)]
        float m_Dimmer = 1;
        [SerializeField]
        ReflectionProbeMode m_Mode = ReflectionProbeMode.Baked;
        [SerializeField]
        ReflectionProbeRefreshMode m_RefreshMode = ReflectionProbeRefreshMode.OnAwake;
        [SerializeField]
        Texture m_CustomTexture;
        [SerializeField]
        Texture m_BakedTexture;
        [SerializeField]
        RenderTexture m_RealtimeTexture;
        [SerializeField]
        FrameSettings m_FrameSettings;

        public ProxyVolumeComponent proxyVolumeReference { get { return m_ProxyVolumeReference; } }
        public InfluenceVolume influenceVolume { get { return m_InfluenceVolume; } }
        public BoundingSphere boundingSphere { get { return m_InfluenceVolume.GetBoundingSphereAt(transform); } }

        public Texture texture
        {
            get
            {
                switch (m_Mode)
                {
                    default:
                        case ReflectionProbeMode.Baked:
                            return bakedTexture;
                        case ReflectionProbeMode.Custom:
                            return customTexture;
                        case ReflectionProbeMode.Realtime:
                            return realtimeTexture;
                }
            }
        }
        public Bounds bounds { get { return m_InfluenceVolume.GetBoundsAt(transform); } }
        public Vector3 captureLocalPosition { get { return m_CaptureLocalPosition; } set { m_CaptureLocalPosition = value; } }
        public float dimmer { get { return m_Dimmer; } }
        public ReflectionProbeMode mode { get { return m_Mode; } }
        public Vector3 influenceRight { get { return transform.right; } }
        public Vector3 influenceUp { get { return transform.up; } }
        public Vector3 influenceForward { get { return transform.forward; } }
        public Vector3 capturePosition
        {
            get { return transform.TransformPoint(m_CaptureLocalPosition); }
            set { m_CaptureLocalPosition = transform.InverseTransformPoint(value); }
        }
        public Vector3 influencePosition { get { return transform.position; } }
        public Texture customTexture { get { return m_CustomTexture; } }
        public Texture bakedTexture { get { return m_BakedTexture; } set { m_BakedTexture = value; }}
        public RenderTexture realtimeTexture { get { return m_RealtimeTexture; } internal set { m_RealtimeTexture = value; } }
        public ReflectionProbeRefreshMode refreshMode { get { return m_RefreshMode; } }
        public FrameSettings frameSettings { get { return m_FrameSettings; } }

        #region Proxy Properties
        public Vector3 proxyRight
        {
            get
            {
                return m_ProxyVolumeReference != null
                    ? m_ProxyVolumeReference.transform.right
                    : influenceRight;
            }
        }
        public Vector3 proxyUp
        {
            get
            {
                return m_ProxyVolumeReference != null
                    ? m_ProxyVolumeReference.transform.up
                    : influenceUp;
            }
        }
        public Vector3 proxyForward
        {
            get
            {
                return m_ProxyVolumeReference != null
                    ? m_ProxyVolumeReference.transform.forward
                    : influenceForward;
            }
        }
        public Vector3 proxyPosition
        {
            get
            {
                return m_ProxyVolumeReference != null
                    ? m_ProxyVolumeReference.transform.position
                    : influencePosition;
            }
        }
        public ShapeType proxyShape
        {
            get
            {
                return m_ProxyVolumeReference != null
                    ? m_ProxyVolumeReference.proxyVolume.shapeType
                    : influenceVolume.shapeType;
            }
        }
        public Vector3 proxyExtents
        {
            get
            {
                return m_ProxyVolumeReference != null
                    ? m_ProxyVolumeReference.proxyVolume.boxSize
                    : influenceVolume.boxBaseSize;
            }
        }
        public bool infiniteProjection { get { return m_ProxyVolumeReference != null && m_ProxyVolumeReference.proxyVolume.infiniteProjection; } }
        #endregion

        public void RequestRealtimeRender()
        {
            if (enabled)
                ReflectionSystem.RequestRealtimeRender(this);
        }

        void OnEnable()
        {
            ReflectionSystem.RegisterProbe(this);
        }

        void OnDisable()
        {
            ReflectionSystem.UnregisterProbe(this);
        }

        void OnValidate()
        {
            if (enabled)
            {
                ReflectionSystem.UnregisterProbe(this);
                ReflectionSystem.RegisterProbe(this);
            }
        }
    }
}
