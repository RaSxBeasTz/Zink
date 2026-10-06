using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System;

namespace Zink.Services.NativeCalling
{
    /// <summary>
    /// Converts Windows FP16 scRGB capture into an SDR stream surface without
    /// clipping HDR highlights or washing out SDR content.
    /// </summary>
    internal sealed class ZinkHdrToSdrGpuConverter : IDisposable
    {
        // Windows HDR commonly presents SDR paper-white near 203 nits, while one
        // scRGB unit represents 80 nits. An exposure of 0.24 maps that paper
        // white to roughly 0.61 linear / 0.80 sRGB, reserving visible headroom
        // for highlights instead of producing the overexposed, clipped picture.
        public const float StreamExposure = 0.24f;
        public const string ToneMapDescription = "scRGB linear -> 203-nit paper-white normalization -> smooth Rec.709 luminance shoulder -> hue-preserving gamut compression -> sRGB, exposure 0.24";

        private const string Shader = @"
Texture2D<float4> S:register(t0); SamplerState L:register(s0);
struct O{float4 p:SV_Position;float2 u:TEXCOORD0;};
O VS(uint i:SV_VertexID){O o;o.u=float2((i<<1)&2,i&2);o.p=float4(o.u*float2(2,-2)+float2(-1,1),0,1);return o;}
float3 linear_to_srgb(float3 x){return lerp(12.92*x,1.055*pow(max(x,0),1.0/2.4)-0.055,step(0.0031308,x));}
float4 PS(O i):SV_Target{
 float3 c=max(S.SampleLevel(L,i.u,0).rgb,0);
 // scRGB 1.0 is 80 nits. Preserve normal desktop white and smoothly roll
 // HDR highlights into the SDR range instead of hard clipping them.
 // Normalize Windows HDR paper-white before mapping into the SDR stream.
 c*=0.24;
 // Compress luminance rather than RGB channels independently. Independent
 // clipping changes channel ratios and makes saturated colours look neon.
 float y=dot(c,float3(0.2126,0.7152,0.0722));
 // Preserve shadows and midtones through 0.70 linear, then apply a C1-smooth
 // exponential shoulder that approaches SDR white without clipping every HDR
 // highlight to the same value. The derivative is 1 at the join, preventing a
 // visible brightness step around light UI backgrounds.
 float shoulderStart=0.70;
 float shoulderRange=0.30;
 float mappedY=y<=shoulderStart
  ? y
  : shoulderStart+shoulderRange*(1.0-exp(-(y-shoulderStart)/shoulderRange));
 c*=mappedY/max(y,0.00001);
 // Keep out-of-gamut highlights hue-preserving. Per-channel saturation makes
 // reds/blues look neon even when overall luminance is correct.
 c/=max(1.0,max(c.r,max(c.g,c.b)));
 return float4(saturate(linear_to_srgb(c)),1);
}";

        private readonly Device _device;
        private readonly VertexShader _vertexShader;
        private readonly PixelShader _pixelShader;
        private readonly SamplerState _sampler;

        public ZinkHdrToSdrGpuConverter(Device device)
        {
            _device = device;
            using var vs = ShaderBytecode.Compile(Shader, "VS", "vs_5_0", ShaderFlags.OptimizationLevel3);
            using var ps = ShaderBytecode.Compile(Shader, "PS", "ps_5_0", ShaderFlags.OptimizationLevel3);
            _vertexShader = new VertexShader(device, vs);
            _pixelShader = new PixelShader(device, ps);
            _sampler = new SamplerState(device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaximumLod = float.MaxValue
            });
        }

        public void Convert(Texture2D source, Texture2D target)
        {
            using var sourceView = new ShaderResourceView(_device, source);
            using var targetView = new RenderTargetView(_device, target);
            var context = _device.ImmediateContext;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.VertexShader.Set(_vertexShader);
            context.PixelShader.Set(_pixelShader);
            context.PixelShader.SetShaderResource(0, sourceView);
            context.PixelShader.SetSampler(0, _sampler);
            context.Rasterizer.SetViewport(0, 0, target.Description.Width, target.Description.Height);
            context.OutputMerger.SetRenderTargets(targetView);
            context.Draw(3, 0);
            context.PixelShader.SetShaderResource(0, null);
            context.OutputMerger.ResetTargets();
        }

        public void Dispose()
        {
            _sampler.Dispose();
            _pixelShader.Dispose();
            _vertexShader.Dispose();
        }
    }
}
