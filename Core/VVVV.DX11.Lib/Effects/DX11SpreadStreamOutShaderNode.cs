﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.ComponentModel.Composition;

using SlimDX;
using SlimDX.DXGI;
using SlimDX.D3DCompiler;
using SlimDX.Direct3D11;
using Device = SlimDX.Direct3D11.Device;
using Buffer = SlimDX.Direct3D11.Buffer;


using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Hosting.Pins.Input;
using VVVV.Hosting.Pins.Output;
using VVVV.Hosting.Pins;


using VVVV.DX11.Internals.Effects;
using VVVV.DX11.Internals.Effects.Pins;
using VVVV.DX11.Internals;

using VVVV.DX11.Lib.Effects;
using VVVV.DX11.Lib.Devices;
using VVVV.DX11;

using VVVV.Core.Model;
using VVVV.Core.Model.FX;
using VVVV.DX11.Internals.Helpers;

using VVVV.DX11.Lib.Rendering;
using FeralTic.DX11;
using FeralTic.DX11.Resources;
using FeralTic.DX11.Utils;
using System.CodeDom.Compiler;


namespace VVVV.DX11.Nodes.Layers
{
    [PluginInfo(Name = "ShaderNode", Category = "DX11", Version = "", Author = "vux")]
    public unsafe class DX11SpreadStreamOutShaderNode : DX11BaseShaderNode, IPluginBase, IPluginEvaluate, IDisposable, IDX11ResourceProvider
    {
        private int tid = 0;

        private DX11ObjectRenderSettings objectsettings = new DX11ObjectRenderSettings();

        private DX11ShaderVariableManager varmanager;
        private Dictionary<DX11RenderContext, DX11ShaderData> deviceshaderdata = new Dictionary<DX11RenderContext, DX11ShaderData>();

        private DX11RenderSettings settings = new DX11RenderSettings();
        private bool shaderupdated;

        private int spmax = 0;

        #region Default Input Pins
        [Input("Geometry In", CheckIfChanged=true)]
        protected Pin<DX11Resource<IDX11Geometry>> FIn;

        [Input("SpreadLayers", DefaultBoolean = true, Visibility = PinVisibility.OnlyInspector)]
        protected ISpread<bool> FInSpread;

        [Input("View",Order = 10001, IsSingle = true)]
        protected ISpread<Matrix> FInView;

        [Input("Projection", Order = 10002, IsSingle = true)]
        protected ISpread<Matrix> FInProjection;

        [Input("As Auto", Order = 10003, IsSingle = true)]
        protected IDiffSpread<bool> FInAsAuto;

        [Input("Auto Layout", Order = 10005, CheckIfChanged = true)]
        protected IDiffSpread<bool> FInAutoLayout;

        [Input("Max Elements", Order = 10004, IsSingle = true)]
        protected IDiffSpread<int> FInMaxElements;

        [Input("Output Layout", Order = 10005, CheckIfChanged = true)]
        protected Pin<InputElement> FInLayout;

        [Input("Custom Semantics", Order = 50000, Visibility = PinVisibility.OnlyInspector)]
        protected Pin<IDX11RenderSemantic> FInSemantics;

        [Input("Resource Semantics", Order = 50001, Visibility = PinVisibility.OnlyInspector)]
        protected Pin<DX11Resource<IDX11RenderSemantic>> FInResSemantics;
        #endregion

        #region Output Pins

        [Output("Geometry Out")]
        protected ISpread<DX11Resource<IDX11Geometry>> FOut;

        [Output("Buffer Out")]
        protected ISpread<DX11Resource<DX11RawBuffer>> FOutBuffer;

        [Output("Technique Valid")]
        protected ISpread<bool> FOutTechniqueValid;

        private IDX11Geometry[] clone   = new IDX11Geometry[64]; 
        private Buffer[] buffer         = new Buffer[64];

        #endregion

        #region Set the shader instance
        public override void SetShader(DX11Effect shader, bool isnew)
        {
            if (isnew) { this.FShader = shader; }

            if (shader.IsCompiled)
            {
                this.FShader = shader;
                this.varmanager.SetShader(shader);
            }

            //Only set technique if new, otherwise do it on update/evaluate
            if (isnew)
            {
                string defaultenum;
                if (shader.IsCompiled)
                {
                    defaultenum = shader.TechniqueNames[0];
                    this.FHost.UpdateEnum(this.TechniqueEnumId, shader.TechniqueNames[0], shader.TechniqueNames);
                    this.varmanager.CreateShaderPins();
                }
                else
                {
                    defaultenum = "";
                    this.FHost.UpdateEnum(this.TechniqueEnumId, "", new string[0]);
                }
            }
            else
            {
                if (shader.IsCompiled)
                {
                    this.FHost.UpdateEnum(this.TechniqueEnumId, shader.TechniqueNames[0], shader.TechniqueNames);
                    this.varmanager.UpdateShaderPins();
                }
            }


            this.shaderupdated = true;
            this.FInvalidate = true;
        }
        #endregion

        #region Constructor
        [ImportingConstructor()]
        public DX11SpreadStreamOutShaderNode(IPluginHost host, IIOFactory factory)
        {
            this.FHost = host;
            this.FFactory = factory;
            this.TechniqueEnumId = Guid.NewGuid().ToString();

            InputAttribute inAttr = new InputAttribute("Technique");
            inAttr.EnumName = this.TechniqueEnumId;
            //inAttr.DefaultEnumEntry = defaultenum;
            inAttr.Order = 1000;
            this.FInTechnique = this.FFactory.CreateDiffSpread<EnumEntry>(inAttr);

            this.varmanager = new DX11ImageShaderVariableManager(host, factory);


        }
        #endregion

        #region Evaluate
        public void Evaluate(int SpreadMax)
        {
            this.spmax = this.CalculateSpreadMax();

            if (!FInSpread[0])
            {
                this.FOut.SliceCount = 1;
                this.FOutBuffer.SliceCount = 1;
                if (this.FOut[0] == null)
                {
                    this.FOut[0] = new DX11Resource<IDX11Geometry>();
                    this.FOutBuffer[0] = new DX11Resource<DX11RawBuffer>();
                }
            } else {
                this.FOut.SliceCount = spmax;
                this.FOutBuffer.SliceCount = spmax;
                for (int i = 0; i < spmax; i++)
                {
                    if (this.FOut[i] == null) 
                    {
                        this.FOut[i] = new DX11Resource<IDX11Geometry>();
                        this.FOutBuffer[i] = new DX11Resource<DX11RawBuffer>();
                    }
                }
            }
            
            if (this.FInvalidate)
            {
                if (this.FShader.IsCompiled)
                {
                    this.FOutCompiled[0] = true;
                    this.FOutTechniqueValid.SliceCount = this.FShader.TechniqueValids.Length;

                    for (int i = 0; i < this.FShader.TechniqueValids.Length; i++)
                    {
                        this.FOutTechniqueValid[i] = this.FShader.TechniqueValids[i];
                    }
                }
                else
                {
                    this.FOutCompiled[0] = false;
                    this.FOutTechniqueValid.SliceCount = 0;
                }
                this.FInvalidate = false;
            }

            if (this.FInTechnique.IsChanged)
            {
                tid = this.FInTechnique[0].Index;

                
                //this.varmanager.RebuildPassCache(tid);
            }

           
            this.FOut.Stream.IsChanged = true;
            this.FOutBuffer.Stream.IsChanged = true;
        }

        #endregion

        #region Calculate Spread Max
        private int CalculateSpreadMax()
        {
            int max = this.varmanager.CalculateSpreadMax();

            if (max == 0 || this.FIn.SliceCount == 0)
            {
                return 0;
            }
            else
            {
                max = Math.Max(this.FIn.SliceCount, max);
                return max;
            }
        }
        #endregion

        #region Update
        public void Update(IPluginIO pin, DX11RenderContext context)
        {
            if (this.CalculateSpreadMax() == 0)
            {
                return;
            }

            Device device = context.Device;
            DeviceContext ctx = context.CurrentDeviceContext;

            if (!this.deviceshaderdata.ContainsKey(context))
            {
                this.deviceshaderdata.Add(context, new DX11ShaderData(context));
                this.deviceshaderdata[context].SetEffect(this.FShader);
            }

            DX11ShaderData shaderdata = this.deviceshaderdata[context];

            for (int i = 0; i < spmax; i++)
            {
                if (this.shaderupdated)
                {
                    shaderdata.SetEffect(this.FShader);
                    shaderdata.Update(this.FInTechnique[i].Index, 0, this.FIn);
                    this.shaderupdated = false;
                }

                if (this.FInEnabled[i] && this.FIn.PluginIO.IsConnected)
                {
                    //Clear shader stages (important here)
                    shaderdata.ResetShaderStages(ctx);


                    if (this.FIn.IsChanged || this.FInTechnique.IsChanged || shaderdata.LayoutValid.Count == 0)
                    {
                        shaderdata.Update(this.FInTechnique[i].Index, 0, this.FIn);
                    
                    }

                    if (shaderdata.IsLayoutValid(0) && this.varmanager.SetGlobalSettings(shaderdata.ShaderInstance,this.settings))
                    {
                        this.OnBeginQuery(context);

                        this.settings = new DX11RenderSettings();
                        this.settings.RenderWidth = 1;
                        this.settings.RenderHeight = 1;
                        this.settings.View = this.FInView[i];
                        this.settings.Projection = this.FInProjection[i];
                        this.settings.ViewProjection = this.settings.View * this.settings.Projection;
                        this.settings.RenderDepth = 1;
                        this.settings.BackBuffer = null;

                        if (this.FInSemantics.PluginIO.IsConnected)
                        {
                            this.settings.CustomSemantics.AddRange(this.FInSemantics.ToArray());
                        }
                        if (this.FInResSemantics.PluginIO.IsConnected)
                        {
                            this.settings.ResourceSemantics.AddRange(this.FInResSemantics.ToArray());
                        }

                        this.varmanager.ApplyGlobal(shaderdata.ShaderInstance);

                        if (this.clone[i] == null || this.FIn.IsChanged || this.FInAsAuto.IsChanged || this.FInMaxElements.IsChanged || this.FInLayout.IsChanged || this.FInAutoLayout.IsChanged)
                        {
                            if (this.buffer[i] != null) { this.buffer[i].Dispose(); }

                            bool customlayout = this.FInLayout.PluginIO.IsConnected || this.FInAutoLayout[i];
                            InputElement[] elems = null;
                            int size = 0;

                            if (this.FInAutoLayout[i])
                            {
                                elems = this.FShader.DefaultEffect.GetTechniqueByIndex(tid).GetPassByIndex(0).GetStreamOutputLayout(out size);
                            }
                            else
                            {
                                if (customlayout)
                                {
                                    elems = this.BindInputLayout(out size);
                                }
                            }

                            #region Vertex Geom
                            if (this.FIn[i][context] is DX11VertexGeometry)
                            {
                                if (!this.FInAsAuto[i])
                                {
                                    DX11VertexGeometry vg = (DX11VertexGeometry)this.FIn[i][context].ShallowCopy();

                                    int vsize = customlayout ? size : vg.VertexSize;
                                    Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, vsize, vg.VerticesCount);
                                    if (customlayout) { vg.VertexSize = vsize; }
                                    vg.VertexBuffer = vbo;

                                    this.clone[i] = vg;
                                    this.buffer[i] = vbo;
                                }
                                else
                                {
                                    DX11VertexGeometry vg = (DX11VertexGeometry)this.FIn[i][context].ShallowCopy();

                                    int maxv = vg.VerticesCount;
                                    if (this.FInMaxElements[i] > 0)
                                    {
                                        maxv = this.FInMaxElements[i];
                                    }

                                    int vsize = customlayout ? size : vg.VertexSize;
                                    Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, vsize, maxv);
                                    vg.VertexBuffer = vbo;
                                    vg.AssignDrawer(new DX11VertexAutoDrawer());
                                    if (customlayout) { vg.VertexSize = vsize; }

                                    this.clone[i] = vg;
                                    this.buffer[i] = vbo;
                                }
                            }
                            #endregion

                            #region Inxexed geom
                            if (this.FIn[i][context] is DX11IndexedGeometry)
                            {
                                if (!this.FInAsAuto[i])
                                {

                                    DX11IndexedGeometry ig = (DX11IndexedGeometry)this.FIn[i][context].ShallowCopy();

                                    int vsize = customlayout ? size : ig.VertexSize;
                                    Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, vsize, ig.VerticesCount);
                                    ig.VertexBuffer = vbo;
                                    if (customlayout) { ig.VertexSize = vsize; }
                                    this.clone[i] = ig;
                                    this.buffer[i] = vbo;
                                }
                                else
                                {
                                    //Need to rebind indexed geom as vertex
                                    DX11IndexedGeometry ig = (DX11IndexedGeometry)this.FIn[i][context];

                                    int maxv = ig.VerticesCount;
                                    if (this.FInMaxElements[i] > 0)
                                    {
                                        maxv = this.FInMaxElements[i];
                                    }

                                    int vsize = customlayout ? size : ig.VertexSize;
                                    Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, vsize, maxv);

                                    //Copy a new Vertex buffer[i] with stream out
                                    DX11VertexGeometry vg = new DX11VertexGeometry(context);
                                    vg.AssignDrawer(new DX11VertexAutoDrawer());
                                    vg.BoundingBox = ig.BoundingBox;
                                    vg.HasBoundingBox = ig.HasBoundingBox;
                                    vg.InputLayout = ig.InputLayout;
                                    vg.Topology = ig.Topology;
                                    vg.VertexBuffer = vbo;
                                    vg.VertexSize = ig.VertexSize;
                                    vg.VerticesCount = ig.VerticesCount;

                                    if (customlayout) { vg.VertexSize = vsize; }

                                    this.clone[i] = vg;
                                    this.buffer[i] = vbo;
                                }
                            }
                            #endregion

                            #region Null geom
                            if (this.FIn[i][context] is DX11NullGeometry)
                            {
                                DX11NullGeometry ng = (DX11NullGeometry)this.FIn[i][context];

                                Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, size, this.FInMaxElements[i]);

                                //Copy a new Vertex buffer[i] with stream out
                                DX11VertexGeometry vg = new DX11VertexGeometry(context);
                                vg.AssignDrawer(new DX11VertexAutoDrawer());
                                vg.BoundingBox = ng.BoundingBox;
                                vg.HasBoundingBox = ng.HasBoundingBox;
                                vg.InputLayout = ng.InputLayout;
                                vg.Topology = ng.Topology;
                                vg.VertexBuffer = vbo;
                                vg.VertexSize = size;
                                vg.VerticesCount = this.FInMaxElements[i];

                                this.clone[i] = vg;
                                this.buffer[i] = vbo;

                            }
                            #endregion


                            #region Index Only geom
                            if (this.FIn[i][context] is DX11IndexOnlyGeometry)
                            {
                                DX11IndexOnlyGeometry ng = (DX11IndexOnlyGeometry)this.FIn[i][context];

                                Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, size, this.FInMaxElements[i]);

                                //Copy a new Vertex buffer[i] with stream out
                                DX11VertexGeometry vg = new DX11VertexGeometry(context);
                                vg.AssignDrawer(new DX11VertexAutoDrawer());
                                vg.BoundingBox = ng.BoundingBox;
                                vg.HasBoundingBox = ng.HasBoundingBox;
                                vg.InputLayout = ng.InputLayout;
                                vg.Topology = ng.Topology;
                                vg.VertexBuffer = vbo;
                                vg.VertexSize = size;
                                vg.VerticesCount = this.FInMaxElements[i];

                                this.clone[i] = vg;
                                this.buffer[i] = vbo;

                            }
                            #endregion

                            if (customlayout) { this.clone[i].InputLayout = elems; }

                            if (this.FOutBuffer[i][context] != null)
                            {
                                this.FOutBuffer[i][context].SRV.Dispose();
                            }

                            if (context.ComputeShaderSupport)
                            {
                                this.FOutBuffer[i][context] = new DX11RawBuffer(context, this.buffer[i]);
                            }
                            else
                            {
                                this.FOutBuffer[i][context] = null;
                            }

                        }

                        ctx.StreamOutput.SetTargets(new StreamOutputBufferBinding(this.buffer[i], 0));
                        shaderdata.SetInputAssembler(ctx, this.FIn[i][context], 0);

                        DX11ObjectRenderSettings ors = new DX11ObjectRenderSettings();
                        ors.DrawCallIndex = 0;
                        ors.Geometry = this.FIn[i][context];
                        ors.WorldTransform = Matrix.Identity;

                        this.varmanager.ApplyPerObject(context, shaderdata.ShaderInstance, ors, 0);


                        shaderdata.ApplyPass(ctx);

                        this.FIn[i][context].Draw();

                        ctx.StreamOutput.SetTargets(null);

                        this.FOut[i][context] = this.clone[i];

                        this.OnEndQuery(context);

                    
                    }
                    else
                    {
                        this.FOut[i][context] = this.FIn[i][context];
                    }
                }
                else
                {
                    this.FOut[i][context] = this.FIn[i][context];
                }
            }
            
        }

        #endregion


        #region Destroy
        public void Destroy(IPluginIO pin, DX11RenderContext context, bool force)
        {
            if (this.deviceshaderdata.ContainsKey(context))
            {
                this.deviceshaderdata[context].Dispose();
                this.deviceshaderdata.Remove(context);
            }
        }
        #endregion

        #region Dispose
        public void Dispose()
        {
            //if (this.effect != null) { this.effect.Dispose(); }
        }
        #endregion

        private InputElement[] BindInputLayout(out int vertexsize)
        {
            InputElement[] inputlayout = new InputElement[this.FInLayout.SliceCount];
            vertexsize = 0;
            for (int i = 0; i < this.FInLayout.SliceCount; i++)
            {

                if (this.FInLayout.PluginIO.IsConnected && this.FInLayout[i] != null)
                {
                    inputlayout[i] = this.FInLayout[i];
                }
                else
                {
                    //Set deault, can do better here
                    inputlayout[i] = new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0);
                }
                vertexsize += FormatHelper.Instance.GetSize(inputlayout[i].Format);
            }
            InputLayoutFactory.AutoIndex(inputlayout);

            return inputlayout;
        }

    }
}