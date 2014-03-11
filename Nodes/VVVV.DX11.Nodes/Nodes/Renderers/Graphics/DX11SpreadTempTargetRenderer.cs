using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using System.ComponentModel.Composition;

using VVVV.PluginInterfaces.V2;
using VVVV.PluginInterfaces.V1;
using VVVV.Utils.VMath;
using VVVV.Utils.VColor;

using SlimDX;
using SlimDX.DXGI;
using SlimDX.Direct3D11;
using Device = SlimDX.Direct3D11.Device;

using VVVV.Hosting.Pins.Config;

using VVVV.Hosting.Pins;


using VVVV.DX11;
using VVVV.DX11.Internals;
using VVVV.DX11.Internals.Helpers;
using VVVV.DX11.Lib.Rendering;
using VVVV.DX11.Lib.Devices;

using FeralTic.Resources;
using FeralTic.DX11;
using FeralTic.DX11.Resources;
using FeralTic.DX11.Queries;


namespace VVVV.DX11
{
    [PluginInfo(Name = "Renderer", Category = "DX11", Version = "SpreadTempTarget", Author = "vux,tonfilm,kopffarben", AutoEvaluate = false)]
    public class DX11SpreadTempRTRendererNode : IDX11RendererProvider, IPluginEvaluate, IDisposable, IDX11Queryable
    {
    	protected IPluginHost FHost;
    	
        #region Inputs
    	[Input("Layer", Order = 1, CheckIfChanged=true)]
        protected Pin<DX11Resource<DX11Layer>> FInLayer;
    	
        [Input("Generate Mip Maps", Order = 4)]
        protected IDiffSpread<bool> FInDoMipMaps;

        [Input("Mip Map Levels", Order = 5)]
        protected IDiffSpread<int> FInMipLevel;
    	
    	[Input("Clear", DefaultValue = 1, Order = 6)]
        protected ISpread<bool> FInClear;

        [Input("Clear Depth", DefaultValue = 1, Order = 6)]
        protected ISpread<bool> FInClearDepth;

        [Input("Background Color", DefaultColor = new double[] { 0, 0, 0, 1 }, Order = 7)]
        protected ISpread<Color4> FInBgColor;

        [Input("AA Samples per Pixel", DefaultEnumEntry = "1", EnumName = "DX11_AASamples",Order=8)]
        protected IDiffSpread<EnumEntry> FInAASamplesPerPixel;

        [Input("Enabled", DefaultValue = 1, Order = 9)]
        protected ISpread<bool> FInEnabled;

        [Input("Enable Depth Buffer", Order = 9,DefaultValue=1)]
        protected IDiffSpread<bool> FInDepthBuffer;

        [Input("View", Order = 10)]
        protected IDiffSpread<Matrix> FInView;

        [Input("Projection", Order = 11)]
        protected IDiffSpread<Matrix> FInProjection;

        [Input("Aspect Ratio", Order = 12, Visibility = PinVisibility.Hidden)]
        protected IDiffSpread<Matrix> FInAspect;

        [Input("Crop", Order = 13, Visibility = PinVisibility.OnlyInspector)]
        protected IDiffSpread<Matrix> FInCrop;

        [Input("ViewPort", Order = 20)]
        protected Pin<Viewport> FInViewPort;

        [Input("Transformation Index",Order=22, DefaultValue = 1, Visibility = PinVisibility.OnlyInspector)]
        protected IDiffSpread<int> FInTI;
        #endregion

        #region Output Pins
        [Output("Buffer Size")]
        protected ISpread<Vector2D> FOutBufferSize;

        [Output("Texture Out")]
        protected ISpread<DX11Resource<DX11Texture2D>> FOutBuffers;

        [Output("AA Texture Out", Visibility=PinVisibility.OnlyInspector)]
        protected ISpread<DX11Resource<DX11Texture2D>> FOutAABuffers;

    	[Output("Query", Order = 200, IsSingle = true)]
        protected ISpread<IDX11Queryable> FOutQueryable;
        #endregion

    	public event DX11QueryableDelegate BeginQuery;
        public event DX11QueryableDelegate EndQuery;

        protected int width;
        protected int height;
        protected SampleDescription sd = new SampleDescription(1, 0);

        protected Dictionary<DX11RenderContext, DX11GraphicsRenderer> renderers = new Dictionary<DX11RenderContext, DX11GraphicsRenderer>();
        protected List<DX11RenderContext> updateddevices = new List<DX11RenderContext>();
        protected List<DX11RenderContext> rendereddevices = new List<DX11RenderContext>();
        protected DepthBufferManager depthmanager;
    	
        private bool genmipmap;
        private int mipmaplevel;

        private Dictionary<DX11RenderContext, DX11RenderTarget2D> targets = new Dictionary<DX11RenderContext, DX11RenderTarget2D>();
        private Dictionary<DX11RenderContext, DX11RenderTarget2D> targetresolve = new Dictionary<DX11RenderContext, DX11RenderTarget2D>();
        private RenderTargetManager rtm;
    	
    	private DX11RenderTarget2D[] temptarget = new DX11RenderTarget2D[64];
    	private DX11RenderTarget2D[] temptargetresolve = new DX11RenderTarget2D[64];
    	
        #region Constructor
        [ImportingConstructor()]
        public DX11SpreadTempRTRendererNode(IPluginHost FHost, IIOFactory iofactory)
        {
            this.depthmanager = new DepthBufferManager(FHost,iofactory);
            this.rtm = new RenderTargetManager(FHost,iofactory);
        }
    	
    	public bool IsEnabled
        {
            get { return this.FInEnabled[0]; }
        }
        #endregion	
    	
        #region Evaluate
    	public void Evaluate(int SpreadMax)
        {
            if (this.FOutQueryable[0] == null) { this.FOutQueryable[0] = this; }
            if (!this.depthmanager.FormatChanged) // do not clear reset if format changed
            {
                this.depthmanager.NeedReset = false;
            }
            else
            {
                this.depthmanager.FormatChanged = false; //Clear flag ok
            }

            this.rendereddevices.Clear();
            this.updateddevices.Clear();

        	this.FOutBuffers.SliceCount = this.FInLayer.SliceCount;
        	this.FOutAABuffers.SliceCount = this.FInLayer.SliceCount;
        	
        	for( int i = 0; i < this.FInLayer.SliceCount; i++)
        	{
				if (this.FOutBuffers[i] == null) { this.FOutBuffers[i] = new DX11Resource<DX11Texture2D>(); }
				if (this.FOutAABuffers[i] == null) { this.FOutAABuffers[i] = new DX11Resource<DX11Texture2D>(); }
			}
        	
            if (this.FInAASamplesPerPixel.IsChanged
              || this.FInDoMipMaps.IsChanged
              || this.FInMipLevel.IsChanged)
            {
                this.sd.Count = Convert.ToInt32(this.FInAASamplesPerPixel[0].Name);
                this.sd.Quality = 0;
                this.genmipmap = this.FInDoMipMaps[0];
                this.mipmaplevel = Math.Max(FInMipLevel[0], 0);
                this.depthmanager.NeedReset = true;
            }

            this.FOutBufferSize[0] = new Vector2D(this.width, this.height);
        }
        #endregion

        #region Update
    	public void Update(IPluginIO pin, DX11RenderContext context)
        {
            Device device = context.Device;

            if (this.updateddevices.Contains(context)) { return; }

            if (!this.renderers.ContainsKey(context))
            {
                this.renderers.Add(context, new DX11GraphicsRenderer(this.FHost, context));
            }

            //Update what's needed
            //Grab a temp target if enabled

            TexInfo ti = this.rtm.GetRenderTarget(context);

            if (ti.w != this.width || ti.h != this.height || !this.targets.ContainsKey(context) || this.FInAASamplesPerPixel.IsChanged)
            {
                this.width = ti.w;
                this.height = ti.h;

                this.depthmanager.NeedReset = true;

                if (targets.ContainsKey(context))
                {
                    context.ResourcePool.Unlock(targets[context]);
                }

                if (targetresolve.ContainsKey(context))
                {
                    context.ResourcePool.Unlock(targetresolve[context]);
                }

            	for ( int i = 0 ; i < this.FInLayer.SliceCount; i++)
            	{
	                int aacount = Convert.ToInt32(this.FInAASamplesPerPixel[i].Name);
	                int aaquality = 0;
	
	                if (aacount > 1)
	                {
	                    temptarget[i] = context.ResourcePool.LockRenderTarget(this.width, this.height, ti.format, new SampleDescription(aacount,aaquality), this.FInDoMipMaps[0], this.FInMipLevel[0]).Element;
	                    temptargetresolve[i] = context.ResourcePool.LockRenderTarget(this.width, this.height, ti.format, new SampleDescription(1, 0), this.FInDoMipMaps[0], this.FInMipLevel[0]).Element;
	
	                    this.FOutBuffers[i][context] = temptargetresolve[i];
	                    this.FOutAABuffers[i][context] = temptarget[i];
	                }
	                else
	                {
	                    //Bind both texture as same output
	                    temptarget[i] = context.ResourcePool.LockRenderTarget(this.width, this.height, ti.format, new SampleDescription(aacount, aaquality), this.FInDoMipMaps[0], this.FInMipLevel[0]).Element;
	  
	                    this.FOutBuffers[i][context] = temptarget[i];
	                    this.FOutAABuffers[i][context] = temptarget[i];
	                }
            	}
            }

            //Update depth manager
            this.depthmanager.Update(context, this.width, this.height, this.sd);

            this.updateddevices.Add(context);
        }
        #endregion

    	#region Render
        public void Render(DX11RenderContext context)
        {
            Device device = context.Device;

            //Just in case
            if (!this.updateddevices.Contains(context))
            {
                this.Update(null, context);
            }

            if (this.rendereddevices.Contains(context)) { return; }

            if (this.BeginQuery != null)
            {
                this.BeginQuery(context);
            }
        	
        
        	for (int layer = 0 ; layer < this.FInLayer.SliceCount ; layer++)
        	{
        		if (this.FInEnabled[layer])
        		{
	                DX11GraphicsRenderer renderer = this.renderers[context];
	
	            	int aacount = Convert.ToInt32(this.FInAASamplesPerPixel[layer].Name);
	            	if (aacount > 1)
	                {
	            		targets[context] = temptarget[layer];
						targetresolve[context] = temptargetresolve[layer];
	                } else {
	                	targets[context] = temptarget[layer];
	                }
	            	
					renderer.EnableDepth = this.FInDepthBuffer[layer];
					renderer.DepthStencil = this.depthmanager.GetDepthStencil(context);
					renderer.DepthMode = this.depthmanager.Mode;
					renderer.SetRenderTargets(targets[context]);
	
	                renderer.SetTargets();
	
	                if (this.FInClearDepth[layer] && this.FInDepthBuffer[layer])
	                {
	                    this.depthmanager.Clear(context);
	                }
	
	                if (this.FInClear[layer])
	                {
	                    renderer.Clear(this.FInBgColor[layer]);
	                }
	
	                if (this.FInLayer.PluginIO.IsConnected)
	                {
	
	                    int rtmax = Math.Max(this.FInProjection.SliceCount, this.FInView.SliceCount);
	                    rtmax = Math.Max(rtmax, this.FInViewPort.SliceCount);
	
	                    DX11RenderSettings settings = new DX11RenderSettings();
	                    settings.ViewportCount = rtmax;
	
	                    bool viewportpop = this.FInViewPort.PluginIO.IsConnected;
	
	                    float cw = (float)this.width;
	                    float ch = (float)this.height;
	
	                    //for (int i = 0; i < rtmax; i++)
	                    {
	                        settings.ViewportIndex = 0;
	                        settings.View = this.FInView[layer];
	
	                        Matrix proj = this.FInProjection[layer];
	                        Matrix aspect = Matrix.Invert(this.FInAspect[layer]);
	                        Matrix crop = Matrix.Invert(this.FInCrop[layer]);
	
	                        settings.Projection = proj * aspect * crop;
	                        settings.ViewProjection = settings.View * settings.Projection;
	                        settings.RenderWidth = this.width;
	                        settings.RenderHeight = this.height;
	                        settings.BackBuffer = (IDX11RWResource)this.FOutBuffers[layer][context];
	                        settings.CustomSemantics.Clear();
	                        settings.ResourceSemantics.Clear();
	
	                        if (viewportpop)
	                        {
	                            context.RenderTargetStack.PushViewport(this.FInViewPort[layer].Normalize(cw, ch));
	                        }
	
	                        //Call render on all layers
	                        //for (int j = 0; j < this.FInLayer.SliceCount; j++)
	                        {
	                            try
	                            {
	                                this.FInLayer[layer][context].RenderSpread(this.FInLayer.PluginIO, context, settings, layer);
	                            }
	                            catch (Exception ex)
	                            {
	                                Console.WriteLine(ex.Message);
	                            }
	                        }
	
	                        if (viewportpop)
	                        {
	                            context.RenderTargetStack.PopViewport();
	                        }
	                    }
	                }
	
	
	                //Post render
	                if (this.sd.Count > 1)
		            {
		                context.CurrentDeviceContext.ResolveSubresource(targets[context].Resource, 0, targetresolve[context].Resource,
		                    0, targets[context].Format);
		            }
		
		            if (this.genmipmap && this.sd.Count == 1)
		            {
		                for (int i = 0; i < this.FOutBuffers.SliceCount; i++)
		                {
		                    context.CurrentDeviceContext.GenerateMips(targets[context].SRV);
		                }
		            }
	
	                renderer.CleanTargets();        			
        		}
        	}

            if (this.EndQuery != null)
            {
                this.EndQuery(context);
            }

            this.rendereddevices.Add(context);
        }
        #endregion

    	#region Destroy
    	public void Destroy(IPluginIO pin, DX11RenderContext context, bool force)
        {
            if (this.renderers.ContainsKey(context))
            {
                this.renderers.Remove(context);
            }

            this.depthmanager.Destroy(context);

            this.OnDestroy(context, force);
        }
    	
        protected void OnDestroy(DX11RenderContext context, bool force)
        {
            //Release lock on target
            if (targets.ContainsKey(context))
            {
                context.ResourcePool.Unlock(targets[context]);
                targets.Remove(context);
            }

            if (targetresolve.ContainsKey(context))
            {
                context.ResourcePool.Unlock(targetresolve[context]);
                targetresolve.Remove(context);
            }

            

        }
        #endregion
    	
        #region Dispose
    	public void Dispose()
        {
            this.depthmanager.Dispose();
            this.OnDispose();
        }
    	
        protected void OnDispose()
        {
        }
        #endregion
    }
}
