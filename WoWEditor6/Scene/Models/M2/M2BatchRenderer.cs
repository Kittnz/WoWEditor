﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpDX;
using WoWEditor6.Graphics;
using WoWEditor6.IO.Files.Models;

namespace WoWEditor6.Scene.Models.M2
{
    class M2BatchRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PerInstanceBuffer
        {
            public Matrix matInstance;
            public Color4 colorMod;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PerModelPassBuffer
        {
            public Matrix uvAnimMatrix;
            public Vector4 modelPassParams;
        }

        public static Mesh Mesh { get; private set; }
        public static Sampler Sampler { get; private set; }

        private static readonly BlendState[] BlendStates = new BlendState[7];

        private static ShaderProgram gNoBlendProgram;
        private static ShaderProgram gBlendProgram;
        private static ShaderProgram gBlendTestProgram;
        private static RasterState gNoCullState;
        private static RasterState gCullState;

        private readonly IM2Animator mAnimator;
        private readonly M2File mModel;
        private bool mIsSyncLoaded;
        private bool mIsSyncLoadRequested;

        private VertexBuffer mVertexBuffer;
        private VertexBuffer mInstanceBuffer;
        private IndexBuffer mIndexBuffer;
        private readonly object mInstanceBufferLock = new object();

        private int mInstanceCount;

        private PerInstanceBuffer[] mActiveInstances = new PerInstanceBuffer[0];
        private readonly Matrix[] mAnimationMatrices = new Matrix[256];

        private readonly Dictionary<int, M2RenderInstance> mFullInstances = new Dictionary<int, M2RenderInstance>();
        private readonly List<M2RenderInstance> mVisibleInstances = new List<M2RenderInstance>();

        private bool mSkipRendering;

        private ConstantBuffer mAnimBuffer;
        private ConstantBuffer mPerPassBuffer;

        public BoundingBox BoundingBox { get { return mModel.BoundingBox; } }

        public M2BatchRenderer(M2File model)
        {
            mModel = model;
            mAnimator = ModelFactory.Instance.CreateAnimator(model);
            mAnimator.SetAnimationByIndex(0);
            StaticAnimationThread.Instance.AddAnimator(mAnimator);
        }

        public virtual void Dispose()
        {
            mSkipRendering = true;
            var vb = mVertexBuffer;
            var ib = mIndexBuffer;
            var instanceBuffer = mInstanceBuffer;
            var cb = mAnimBuffer;
            var pb = mPerPassBuffer;

            WorldFrame.Instance.Dispatcher.BeginInvoke(() =>
            {
                if (vb != null)
                    vb.Dispose();
                if (ib != null)
                    ib.Dispose();
                if (instanceBuffer != null)
                    instanceBuffer.Dispose();
                if (cb != null)
                    cb.Dispose();
                if (pb != null)
                    pb.Dispose();
            });

            StaticAnimationThread.Instance.RemoveAnimator(mAnimator);
        }

        public void OnFrame()
        {
            if(mIsSyncLoaded == false)
            {
                if (mIsSyncLoadRequested)
                    return;

                if (WorldFrame.Instance.MapManager.IsInitialLoad)
                    SyncLoad();
                else
                {
                    WorldFrame.Instance.Dispatcher.BeginInvoke(SyncLoad);
                    mIsSyncLoadRequested = true;
                    return;
                }
            }

            if (mSkipRendering)
                return;

            if (WorldFrame.Instance.HighlightModelsInBrush)
            {
                var brushPosition = Editing.EditManager.Instance.MousePosition;
                var highlightRadius = Editing.EditManager.Instance.OuterRadius;
                UpdateBrushHighlighting(brushPosition, highlightRadius);
            }

            UpdateVisibleInstances();
            if (mInstanceCount == 0)
                return;

            Mesh.UpdateIndexBuffer(mIndexBuffer);
            Mesh.UpdateVertexBuffer(mVertexBuffer);
            Mesh.UpdateInstanceBuffer(mInstanceBuffer);

            if (mAnimator.GetBones(mAnimationMatrices))
                mAnimBuffer.UpdateData(mAnimationMatrices);

            Mesh.Program.SetVertexConstantBuffer(2, mAnimBuffer);
            Mesh.Program.SetVertexConstantBuffer(3, mPerPassBuffer);

            foreach (var pass in mModel.Passes)
            {
                var cullingDisabled = (pass.RenderFlag & 0x04) != 0;
                Mesh.UpdateRasterizerState(cullingDisabled ? gNoCullState : gCullState);
                Mesh.UpdateBlendState(BlendStates[pass.BlendMode]);

                var oldProgram = Mesh.Program;
                if (pass.BlendMode == 1)
                    Mesh.Program = gBlendTestProgram;
                else
                    Mesh.Program = (pass.BlendMode > 0 ? gBlendProgram : gNoBlendProgram);
                if (Mesh.Program != oldProgram)
                    Mesh.Program.Bind();

                var unlit = ((pass.RenderFlag & 0x01) != 0) ? 0.0f : 1.0f;
                var unfogged = ((pass.RenderFlag & 0x02) != 0) ? 0.0f : 1.0f;

                Matrix uvAnimMat;
                mAnimator.GetUvAnimMatrix(pass.TexAnimIndex, out uvAnimMat);

                mPerPassBuffer.UpdateData(new PerModelPassBuffer()
                {
                    uvAnimMatrix = uvAnimMat,
                    modelPassParams = new Vector4(unlit, unfogged, 0.0f, 0.0f)
                });

                Mesh.StartVertex = 0;
                Mesh.StartIndex = pass.StartIndex;
                Mesh.IndexCount = pass.IndexCount;
                Mesh.Program.SetPixelTexture(0, pass.Textures.First());
                Mesh.Draw(mInstanceCount);
            }
        }

        public bool RemoveInstance(int uuid)
        {
            lock(mFullInstances)
            {
                M2RenderInstance inst;
                if (mFullInstances.TryGetValue(uuid, out inst) == false)
                    return false;

                --inst.NumReferences;
                if (inst.NumReferences > 0)
                    return false;

                mFullInstances.Remove(uuid);
            }

            lock (mInstanceBufferLock)
            {
                for (var i = 0; i < mVisibleInstances.Count; ++i)
                {
                    if (mVisibleInstances[i].Uuid == uuid)
                    {
                        mVisibleInstances.RemoveAt(i);
                        break;
                    }
                }
            }

            lock (mFullInstances)
                return mFullInstances.Count == 0;
        }

        public M2RenderInstance AddInstance(int uuid, Vector3 position, Vector3 rotation, Vector3 scaling)
        {
            M2RenderInstance inst;
            // ReSharper disable once InconsistentlySynchronizedField
            if (mFullInstances.TryGetValue(uuid, out inst))
            {
                ++inst.NumReferences;
                return inst;
            }

            var instance = new M2RenderInstance(uuid, position, rotation, scaling, this);
            lock (mFullInstances)
            {
                mFullInstances.Add(uuid, instance);
                if (!WorldFrame.Instance.ActiveCamera.Contains(ref instance.BoundingBox))
                    return instance;

                lock (mInstanceBufferLock)
                    mVisibleInstances.Add(instance);
                return instance;
            }
        }

        public void PushMapReference(M2Instance instance)
        {
            M2RenderInstance inst;
            lock (mFullInstances)
            {
                if (mFullInstances.TryGetValue(instance.Uuid, out inst) == false)
                    return;
            }

            inst.IsUpdated = true;

            lock (mInstanceBufferLock)
                mVisibleInstances.Add(inst);
        }

        public void ViewChanged()
        {
            lock(mInstanceBufferLock)
                mVisibleInstances.Clear();

            lock(mFullInstances)
                foreach (var pair in mFullInstances)
                    pair.Value.IsUpdated = false;
        }

        private void UpdateBrushHighlighting(Vector3 brushPosition, float radius)
        {
            lock(mInstanceBufferLock)
            {
                foreach (var t in mVisibleInstances)
                    t.UpdateBrushHighlighting(brushPosition, radius);
            }
        }

        private void UpdateVisibleInstances()
        {
            lock(mInstanceBufferLock)
            {
                if (mActiveInstances.Length < mVisibleInstances.Count)
                    mActiveInstances = new PerInstanceBuffer[mVisibleInstances.Count];

                for (var i = 0; i < mVisibleInstances.Count; ++i)
                {
                    mActiveInstances[i].matInstance = mVisibleInstances[i].InstanceMatrix;
                    mActiveInstances[i].colorMod = mVisibleInstances[i].HighlightColor;
                }

                mInstanceCount = mVisibleInstances.Count;
                if (mInstanceCount == 0)
                    return;

                mInstanceBuffer.UpdateData(mActiveInstances);
            }
        }

        private void SyncLoad()
        {
            if(mModel.Vertices.Length ==0 || mModel.Indices.Length == 0 || mModel.Passes.Count == 0)
            {
                mIsSyncLoaded = true;
                mSkipRendering = true;
                return;
            }

            var ctx = WorldFrame.Instance.GraphicsContext;
            mVertexBuffer = new VertexBuffer(ctx);
            // ReSharper disable once InconsistentlySynchronizedField
            mInstanceBuffer = new VertexBuffer(ctx);
            mIndexBuffer = new IndexBuffer(ctx);

            mVertexBuffer.UpdateData(mModel.Vertices);
            mIndexBuffer.UpdateData(mModel.Indices);

            mAnimBuffer = new ConstantBuffer(ctx);
            mAnimBuffer.UpdateData(mAnimationMatrices);

            mPerPassBuffer = new ConstantBuffer(ctx);
            mPerPassBuffer.UpdateData(new PerModelPassBuffer()
            {
                uvAnimMatrix = Matrix.Identity,
                modelPassParams = Vector4.Zero
            });

            mIsSyncLoaded = true;
        }

        public static void Initialize(GxContext context)
        {
            Mesh = new Mesh(context)
            {
                Stride = IO.SizeCache<M2Vertex>.Size,
                InstanceStride = IO.SizeCache<PerInstanceBuffer>.Size,
                DepthState = {DepthEnabled = true}
            };

            Mesh.BlendState.Dispose();
            Mesh.IndexBuffer.Dispose();
            Mesh.VertexBuffer.Dispose();

            Mesh.AddElement("POSITION", 0, 3);
            Mesh.AddElement("BLENDWEIGHT", 0, 4, DataType.Byte, true);
            Mesh.AddElement("BLENDINDEX", 0, 4, DataType.Byte);
            Mesh.AddElement("NORMAL", 0, 3);
            Mesh.AddElement("TEXCOORD", 0, 2);
            Mesh.AddElement("TEXCOORD", 1, 2);

            Mesh.AddElement("TEXCOORD", 2, 4, DataType.Float, false, 1, true);
            Mesh.AddElement("TEXCOORD", 3, 4, DataType.Float, false, 1, true);
            Mesh.AddElement("TEXCOORD", 4, 4, DataType.Float, false, 1, true);
            Mesh.AddElement("TEXCOORD", 5, 4, DataType.Float, false, 1, true);
            Mesh.AddElement("COLOR", 0, 4, DataType.Float, false, 1, true);

            var program = new ShaderProgram(context);
            program.SetVertexShader(Resources.Shaders.M2VertexInstanced);
            program.SetPixelShader(Resources.Shaders.M2Pixel);

            Mesh.Program = program;

            Sampler = new Sampler(context)
            {
                AddressMode = SharpDX.Direct3D11.TextureAddressMode.Wrap,
                Filter = SharpDX.Direct3D11.Filter.MinMagMipLinear
            };

            for (var i = 0; i < BlendStates.Length; ++i)
                BlendStates[i] = new BlendState(context);

            BlendStates[0] = new BlendState(context)
            {
                BlendEnabled = false
            };

            BlendStates[1] = new BlendState(context)
            {
                BlendEnabled = true,
                SourceBlend = SharpDX.Direct3D11.BlendOption.One,
                DestinationBlend = SharpDX.Direct3D11.BlendOption.Zero,
                SourceAlphaBlend = SharpDX.Direct3D11.BlendOption.One,
                DestinationAlphaBlend = SharpDX.Direct3D11.BlendOption.Zero
            };

            BlendStates[2] = new BlendState(context)
            {
                BlendEnabled = true,
                SourceBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha,
                DestinationBlend = SharpDX.Direct3D11.BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha,
                DestinationAlphaBlend = SharpDX.Direct3D11.BlendOption.InverseSourceAlpha
            };

            BlendStates[3] = new BlendState(context)
            {
                BlendEnabled = true,
                SourceBlend = SharpDX.Direct3D11.BlendOption.SourceColor,
                DestinationBlend = SharpDX.Direct3D11.BlendOption.DestinationColor,
                SourceAlphaBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha,
                DestinationAlphaBlend = SharpDX.Direct3D11.BlendOption.DestinationAlpha
            };

            BlendStates[4] = new BlendState(context)
            {
                BlendEnabled = true,
                SourceBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha,
                DestinationBlend = SharpDX.Direct3D11.BlendOption.One,
                SourceAlphaBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha,
                DestinationAlphaBlend = SharpDX.Direct3D11.BlendOption.One
            };

            BlendStates[5] = new BlendState(context)
            {
                BlendEnabled = true,
                SourceBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha,
                DestinationBlend = SharpDX.Direct3D11.BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha,
                DestinationAlphaBlend = SharpDX.Direct3D11.BlendOption.InverseSourceAlpha
            };

            BlendStates[6] = new BlendState(context)
            {
                BlendEnabled = true,
                SourceBlend = SharpDX.Direct3D11.BlendOption.DestinationColor,
                DestinationBlend = SharpDX.Direct3D11.BlendOption.SourceColor,
                SourceAlphaBlend = SharpDX.Direct3D11.BlendOption.DestinationAlpha,
                DestinationAlphaBlend = SharpDX.Direct3D11.BlendOption.SourceAlpha
            };

            gNoBlendProgram = program;

            gBlendProgram = new ShaderProgram(context);
            gBlendProgram.SetPixelShader(Resources.Shaders.M2PixelBlend);
            gBlendProgram.SetVertexShader(Resources.Shaders.M2VertexInstanced);

            gBlendTestProgram = new ShaderProgram(context);
            gBlendTestProgram.SetPixelShader(Resources.Shaders.M2PixelBlendAlpha);
            gBlendTestProgram.SetVertexShader(Resources.Shaders.M2VertexInstanced);

            gNoCullState = new RasterState(context) { CullEnabled = false };
            gCullState = new RasterState(context) { CullEnabled = true };
        }
    }
}
