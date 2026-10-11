using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace ClearFolder {
    internal sealed class WaterEffect : ShaderEffect {
        public static readonly DependencyProperty InputProperty=RegisterPixelShaderSamplerProperty("Input",typeof(WaterEffect),0,SamplingMode.Bilinear);
        public static readonly DependencyProperty ParametersProperty=DependencyProperty.Register("Parameters",typeof(Point4D),typeof(WaterEffect),new UIPropertyMetadata(new Point4D(1,1,0,1.12),PixelShaderConstantCallback(0)));
        public static readonly DependencyProperty WaveAProperty=DependencyProperty.Register("WaveA",typeof(Point4D),typeof(WaterEffect),new UIPropertyMetadata(new Point4D(0,0,10,0),PixelShaderConstantCallback(1)));
        public static readonly DependencyProperty WaveBProperty=DependencyProperty.Register("WaveB",typeof(Point4D),typeof(WaterEffect),new UIPropertyMetadata(new Point4D(0,0,10,0),PixelShaderConstantCallback(2)));
        public static readonly DependencyProperty WaveCProperty=DependencyProperty.Register("WaveC",typeof(Point4D),typeof(WaterEffect),new UIPropertyMetadata(new Point4D(0,0,10,0),PixelShaderConstantCallback(3)));
        static PixelShader shader;
        static string failure;
        public Brush Input {get{return (Brush)GetValue(InputProperty);}set{SetValue(InputProperty,value);}}
        internal Point4D Parameters {get{return (Point4D)GetValue(ParametersProperty);}set{SetValue(ParametersProperty,value);}}
        internal Point4D WaveA {get{return (Point4D)GetValue(WaveAProperty);}set{SetValue(WaveAProperty,value);}}
        internal Point4D WaveB {get{return (Point4D)GetValue(WaveBProperty);}set{SetValue(WaveBProperty,value);}}
        internal Point4D WaveC {get{return (Point4D)GetValue(WaveCProperty);}set{SetValue(WaveCProperty,value);}}
        WaterEffect(PixelShader compiled) {
            PixelShader=compiled;UpdateShaderValue(InputProperty);UpdateShaderValue(ParametersProperty);
            UpdateShaderValue(WaveAProperty);UpdateShaderValue(WaveBProperty);UpdateShaderValue(WaveCProperty);
        }
        internal static WaterEffect Create(out string description) {
            if(!RenderCapability.IsPixelShaderVersionSupported(3,0)) {description="WPF 合成动画；硬件不支持 PS 3.0，水波折射不可用";return null;}
            try {
                if(shader==null&&failure==null) {
                    byte[] code=Compile();shader=new PixelShader {ShaderRenderMode=ShaderRenderMode.HardwareOnly};
                    using(var stream=new MemoryStream(code))shader.SetStreamSource(stream);shader.Freeze();
                }
                if(shader==null){description=failure;return null;}
                description="WPF 系统合成动画 + PS 3.0 硬件像素着色器（Render Tier "+(RenderCapability.Tier>>16)+"）";
                return new WaterEffect(shader);
            }catch(Exception ex){failure="WPF 合成动画；水波着色器加载失败："+ex.Message;description=failure;return null;}
        }
        const string Source=@"
sampler2D source : register(s0);
float4 params : register(c0);
float4 waveA : register(c1);
float4 waveB : register(c2);
float4 waveC : register(c3);
float3 ripple(float2 uv,float4 wave) {
    float2 delta=(uv-wave.xy)*params.xy;
    float distance=max(length(delta),0.001);
    float age=wave.z;
    float front=age*170.0;
    float envelope=exp(-pow((distance-front)/27.0,2.0))*exp(-age*1.45)*wave.w;
    float phase=distance*0.18-age*15.0;
    float shift=sin(phase)*envelope*params.z;
    // A signed, traveling light/dark band remains visible on uniform dark glass.
    // It shares the local wave envelope and strength; there is no persistent tint.
    float lighting=sin(phase+0.65)*envelope*min(params.z/4.0,1.5)*0.045;
    return float3(delta/distance*shift/params.xy,lighting);
}
float4 main(float2 uv:TEXCOORD):COLOR {
    float3 wave=ripple(uv,waveA)+ripple(uv,waveB)+ripple(uv,waveC);
    float4 color=tex2D(source,saturate(uv+wave.xy));
    color.rgb=saturate((color.rgb-0.5)*params.w+0.5);
    float luminance=dot(color.rgb,float3(0.2126,0.7152,0.0722));
    float lighting=clamp(wave.z,-0.055,0.055)*(1.0-0.35*luminance);
    color.rgb=saturate(color.rgb+lighting*color.a);
    return color;
}";
        static byte[] Compile() {
            byte[] bytes=Encoding.ASCII.GetBytes(Source);IntPtr code=IntPtr.Zero,errors=IntPtr.Zero;
            try {
                int result=D3DCompile(bytes,new UIntPtr((uint)bytes.Length),null,IntPtr.Zero,IntPtr.Zero,"main","ps_3_0",1u<<15,0,out code,out errors);
                if(result<0||code==IntPtr.Zero)throw new InvalidOperationException(errors!=IntPtr.Zero?ReadText(errors):"D3DCompile 返回 "+result);
                UIntPtr length=Size(code);long count=(long)length.ToUInt64();
                if(count<=0||count>1024*1024)throw new InvalidDataException("着色器大小无效。");
                byte[] compiled=new byte[(int)count];Marshal.Copy(Buffer(code),compiled,0,compiled.Length);return compiled;
            }finally{if(code!=IntPtr.Zero)Marshal.Release(code);if(errors!=IntPtr.Zero)Marshal.Release(errors);}
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetBuffer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate UIntPtr GetSize(IntPtr self);
        static IntPtr Buffer(IntPtr blob){return ((GetBuffer)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob),3*IntPtr.Size),typeof(GetBuffer)))(blob);}
        static UIntPtr Size(IntPtr blob){return ((GetSize)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob),4*IntPtr.Size),typeof(GetSize)))(blob);}
        static string ReadText(IntPtr blob){return Marshal.PtrToStringAnsi(Buffer(blob),(int)Math.Min(4096,Size(blob).ToUInt64()));}
        [DllImport("d3dcompiler_47.dll",CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Ansi)]
        static extern int D3DCompile(byte[] source,UIntPtr length,string name,IntPtr defines,IntPtr include,string entry,string target,uint flags,uint flags2,out IntPtr code,out IntPtr errors);
    }
}
