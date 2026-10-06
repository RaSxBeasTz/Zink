using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace Zink.Services.NativeCalling
{
    internal sealed class ZinkHdrGpuConverter : IDisposable
    {
        private const string Shader = @"
Texture2D<float4> S:register(t0); SamplerState L:register(s0);
struct O{float4 p:SV_Position;float2 u:TEXCOORD0;};
O VS(uint i:SV_VertexID){O o;o.u=float2((i<<1)&2,i&2);o.p=float4(o.u*float2(2,-2)+float2(-1,1),0,1);return o;}
float3 pq(float3 x){
 float3 c=mul(float3x3(0.627404,0.329282,0.043314,0.069097,0.919540,0.011361,0.016392,0.088013,0.895595),max(x,0));
 // Windows FP16 scRGB is absolute: one linear unit represents 80 nits.
 // PQ is normalized to 10,000 nits, hence each scRGB unit is 80/10000.
 // Windows already raises SDR desktop white above 1.0 according to its HDR
 // SDR-content brightness setting, so applying 203 nits here would scale it twice.
 c=saturate(c*0.008);float3 p=pow(c,2610.0/16384.0);return pow((3424.0/4096.0+(2413.0/128.0)*p)/(1+(2392.0/128.0)*p),2523.0/32.0);}
float3 yuv(float2 u){float3 r=pq(S.SampleLevel(L,u,0).rgb);float y=dot(r,float3(.2627,.678,.0593));return float3((64+876*y)/1023,(512+896*(r.b-y)/1.8814)/1023,(512+896*(r.r-y)/1.4746)/1023);}
float Y(O i):SV_Target{return yuv(i.u).x;}
// Linear sampling at the chroma-pixel centre performs the 2x2 source reduction
// in hardware. This replaces four complete matrix/PQ evaluations per UV pixel.
float2 UV(O i):SV_Target{return yuv(i.u).yz;}";

        private readonly D3D11Device _device;
        private readonly VertexShader _vs;
        private readonly PixelShader _y;
        private readonly PixelShader _uv;
        private readonly SamplerState _sampler;

        public ZinkHdrGpuConverter(D3D11Device device)
        {
            _device = device;
            using var vs = ShaderBytecode.Compile(Shader, "VS", "vs_5_0", ShaderFlags.OptimizationLevel3);
            using var y = ShaderBytecode.Compile(Shader, "Y", "ps_5_0", ShaderFlags.OptimizationLevel3);
            using var uv = ShaderBytecode.Compile(Shader, "UV", "ps_5_0", ShaderFlags.OptimizationLevel3);
            _vs = new VertexShader(device, vs);
            _y = new PixelShader(device, y);
            _uv = new PixelShader(device, uv);
            _sampler = new SamplerState(device, new SamplerStateDescription { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue });
        }

        public void Convert(Texture2D source, Texture2D target)
        {
            using var device3 = _device.QueryInterface<SharpDX.Direct3D11.Device3>();
            using var srv = new ShaderResourceView(_device, source);
            var yd = new RenderTargetViewDescription1 { Format = Format.R16_UNorm, Dimension = RenderTargetViewDimension.Texture2D };
            yd.Texture2D.MipSlice = 0; yd.Texture2D.PlaneSlice = 0;
            var uvd = new RenderTargetViewDescription1 { Format = Format.R16G16_UNorm, Dimension = RenderTargetViewDimension.Texture2D };
            uvd.Texture2D.MipSlice = 0; uvd.Texture2D.PlaneSlice = 1;
            using var yr = new RenderTargetView1(device3, target, yd);
            using var uvr = new RenderTargetView1(device3, target, uvd);
            var c = _device.ImmediateContext;
            c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            c.VertexShader.Set(_vs); c.PixelShader.SetShaderResource(0, srv); c.PixelShader.SetSampler(0, _sampler);
            c.Rasterizer.SetViewport(0, 0, target.Description.Width, target.Description.Height);
            c.OutputMerger.SetRenderTargets(yr); c.PixelShader.Set(_y); c.Draw(3, 0);
            c.Rasterizer.SetViewport(0, 0, target.Description.Width / 2f, target.Description.Height / 2f);
            c.OutputMerger.SetRenderTargets(uvr); c.PixelShader.Set(_uv); c.Draw(3, 0);
            c.PixelShader.SetShaderResource(0, null); c.OutputMerger.ResetTargets();
        }

        public void Dispose() { _sampler.Dispose(); _uv.Dispose(); _y.Dispose(); _vs.Dispose(); }
    }
}
