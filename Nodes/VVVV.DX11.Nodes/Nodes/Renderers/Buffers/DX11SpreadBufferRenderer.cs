using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VVVV.PluginInterfaces.V2;
using VVVV.PluginInterfaces.V1;
using SlimDX;
using VVVV.Utils.VMath;

using VVVV.DX11.Lib.Devices;
using SlimDX.Direct3D11;
using System.ComponentModel.Composition;
using VVVV.Hosting.Pins;
using VVVV.DX11.Internals.Helpers;
using VVVV.DX11.Internals;
using VVVV.DX11.Internals.Effects;

using VVVV.DX11.Lib.Rendering;
using FeralTic.DX11.Queries;
using FeralTic.DX11.Resources;
using FeralTic.DX11;
using System.IO;

namespace VVVV.DX11.Nodes
{
    [PluginInfo(Name = "Renderer", Category = "DX11", Version = "SpreadBuffer", Author = "vux", AutoEvaluate = false)]
    public class DX11SpreadBufferRendererNode : IPluginEvaluate, IDX11RendererProvider, IDisposable, IDX11Queryable
    {
        protected IPluginHost FHost;

        [Input("Layer", Order = 1)]
        protected Pin<DX11Resource<DX11Layer>> FInLayer;

        [Input("Element Count", Order = 8, DefaultValue = 512)]
        protected IDiffSpread<int> FInElementCount;

        [Input("Stride", Order = 8, DefaultValue = 16)]
        protected IDiffSpread<int> FInStride;

        [Input("Appendable", Order = 8, DefaultValue = 0)]
        protected IDiffSpread<bool> FInAppendable;

        [Input("Reset Counter",IsBang=true )]
        protected IDiffSpread<bool> FInResetCounter;

        [Input("Reset Counter Value")]
        protected IDiffSpread<int> FInResetCounterValue;

        [Input("Enabled", DefaultValue = 1, Order = 15)]
        protected ISpread<bool> FInEnabled;

        [Input("View", Order = 16)]
        protected IDiffSpread<Matrix> FInView;

        [Input("Projection", Order = 17)]
        protected IDiffSpread<Matrix> FInProjection;

        [Output("Buffers")]
        protected ISpread<DX11Resource<IDX11RWStructureBuffer>> FOutBuffers;

        [Output("Query", Order = 200, IsSingle = true)]
        protected ISpread<IDX11Queryable> FOutQueryable;

        protected int[] cnt		= new int[64];
        protected int[] stride	= new int[64];

    	protected DX11RWStructuredBuffer tmpBuffer;
    	private DX11RWStructuredBuffer[] brushBuffer = new DX11RWStructuredBuffer[64];
        protected List<DX11RenderContext> updateddevices = new List<DX11RenderContext>();
        protected List<DX11RenderContext> rendereddevices = new List<DX11RenderContext>();

 		private bool init  = true;

        public event DX11QueryableDelegate BeginQuery;

        public event DX11QueryableDelegate EndQuery;

        private DX11RenderSettings settings = new DX11RenderSettings();
        private RWStructuredBufferRenderSemantic rwbuffersemantic = new RWStructuredBufferRenderSemantic("MYBUFFER", true);

        [ImportingConstructor()]
        public DX11SpreadBufferRendererNode(IPluginHost FHost)
        {
            //this.settings.CustomSemantics.Add(this.rwbuffersemantic);
        }

        public void Evaluate(int SpreadMax)
        {
            this.rendereddevices.Clear();
            this.updateddevices.Clear();


        	this.FOutBuffers.SliceCount = this.FInLayer.SliceCount;
        	
        	for ( int i = 0 ; i < this.FInLayer.SliceCount ; i++)
        	{
	            if (this.FOutBuffers[i] == null)
	            {
	                this.FOutBuffers[i] = new DX11Resource<IDX11RWStructureBuffer>();
	            }
        	}
        	
            if (this.FOutQueryable[0] == null) { this.FOutQueryable[0] = this; }

        }

        public bool IsEnabled
        {
            get { return this.FInEnabled[0]; }
        }

        public void Render(DX11RenderContext context)
        {
            Device device = context.Device;
            DeviceContext ctx = context.CurrentDeviceContext;

            //Just in case
            if (!this.updateddevices.Contains(context))
            {
                this.Update(null, context);
            }

            if (!this.FInLayer.PluginIO.IsConnected) { return; }

            if (this.rendereddevices.Contains(context)) { return; }

        	
            if (this.FInEnabled[0])
            {
                if (this.BeginQuery != null)
                {
                    this.BeginQuery(context);
                }
				
                context.CurrentDeviceContext.OutputMerger.SetTargets(new RenderTargetView[0]);   
            	
	        	for (int i = 0; i < this.FInLayer.SliceCount; i++)
	            {
	            	if (init || FInLayer.IsChanged || FInElementCount[i] != cnt[i] || FInStride[i] != stride[i])
	            	{
	            		cnt[i]		= FInElementCount[i];
	            		stride[i]	= FInStride[i];
	            		
	            		if (this.brushBuffer[i] != null) { this.brushBuffer[i].Dispose(); }
						if (this.FOutBuffers[i] != null) { this.FOutBuffers[i].Dispose(context); }
	            		
	            		eDX11BufferMode mode = this.FInAppendable[i] ? eDX11BufferMode.Append : eDX11BufferMode.Default;	
	             		brushBuffer[i] = new DX11RWStructuredBuffer(context.Device, cnt[i], stride[i], mode);
	            		
	            		if (i == this.FOutBuffers.SliceCount-1) { init = false; }
	            	}
					
	                settings.ViewportIndex = 0;
	                settings.ViewportCount = 1;
	                settings.View = this.FInView[i];
	                settings.Projection = this.FInProjection[i];
	                settings.ViewProjection = settings.View * settings.Projection;
	                settings.RenderWidth = FInElementCount[i];
	                settings.RenderHeight = 1;
	                settings.RenderDepth = 1;
	          		settings.BackBuffer = brushBuffer[i];
	               
					if (this.FInResetCounter[i])
	                {
	                    settings.ResetCounter = true;
	                    settings.CounterValue = this.FInResetCounterValue[i];
	                }
	                else
	                {
	                    settings.ResetCounter = false;
	                }
	
	              	this.FInLayer[i][context].RenderSpread(this.FInLayer.PluginIO, context, settings,i);
	            	this.FOutBuffers[i][context] = brushBuffer[i];
	            }
        	
            	
           	    if (this.EndQuery != null)
                {
                    this.EndQuery(context);
                }
				
            }
			
        }

        public void Update(IPluginIO pin, DX11RenderContext context)
        {
        	/*for ( int i = 0 ; i < this.FInLayer.SliceCount ; i++)
        	{
	            if (reset || !this.FOutBuffers[i].Contains(context))
	            {
	                this.DisposeBuffers(context);
	
	                eDX11BufferMode mode = this.FInAppendable[i] ? eDX11BufferMode.Append : eDX11BufferMode.Default;

					//brushBuffer[i] = new DX11RWStructuredBuffer(context.Device, this.cnt, this.stride, mode);
	
	                this.FOutBuffers[i][context] = brushBuffer[i];
	            }
            }*/

        	
            this.updateddevices.Add(context);
        }

        public void Destroy(IPluginIO pin, DX11RenderContext OnDevice, bool force)
        {
            //this.DisposeBuffers(OnDevice.Device);
        }

        #region Dispose Buffers
        private void DisposeBuffers(DX11RenderContext context)
        {
            for (int i = 0; i < this.FOutBuffers.SliceCount; i++)
            {
                if (this.FOutBuffers[i] != null) { this.FOutBuffers[i].Dispose(context); }
            }
        	for (int i = 0; i < this.brushBuffer.Length; i++)
            {
                if (this.brushBuffer[i] != null) { this.brushBuffer[i].Dispose(); }
            }
        	
        }
        #endregion

        public void Dispose()
        {
            for (int i = 0; i < this.FOutBuffers.SliceCount; i++)
            {
                if (this.FOutBuffers[i] != null) { this.FOutBuffers[i].Dispose(); }
            }
        	for (int i = 0; i < this.brushBuffer.Length; i++)
            {
            	if (this.brushBuffer[i] != null) { this.brushBuffer[i].Dispose(); }
            }
        }
    }
}
