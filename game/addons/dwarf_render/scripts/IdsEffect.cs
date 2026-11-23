using System;
using Godot;

namespace DwarfRender;

/// <summary>
/// The object-id image, computed from the art pass's own depth and normal-roughness buffers (a compositor effect after the
/// opaque pass) instead of rendering the whole scene a second time into an ids viewport: that second render was every draw
/// call and primitive again whenever any ASCII showed. Writes per art pixel R = glyph group / 255, G = linear view depth
/// (0 = sky), B = a hash of the world tile under the pixel, exactly what <c>post_ids.gdshader</c> wrote.
/// </summary>
[GlobalClass]
public partial class IdsEffect : CompositorEffect
{
	const string Source = @"
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0) uniform sampler2D depth_tex;
layout(set = 0, binding = 1) uniform sampler2D nr_tex;
layout(rgba16f, set = 0, binding = 2) uniform restrict writeonly image2D ids_out;
layout(set = 0, binding = 3, std140) uniform Params {
	mat4 inv_view;   // camera to world
	vec4 info;       // x, y: size; z: reverse z; w: orthographic
	vec4 cam;        // near, far, half height (ortho: world units; perspective: tan of half the vertical fov), aspect
} p;

// The view-space point under a pixel, from the camera's own numbers (Godot: OpenGL-style projection, depth remapped to
// [0, 1], reversed with reverse z).
vec3 view_pos(vec2 uv, float d) {
	float n = p.cam.x, f = p.cam.y;
	float zn = p.info.z > 0.5 ? 1.0 - 2.0 * d : 2.0 * d - 1.0;
	vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
	if (p.info.w > 0.5) {
		float z = -(zn * (f - n) + (f + n)) * 0.5;
		return vec3(ndc.x * p.cam.z * p.cam.w, ndc.y * p.cam.z, z);
	}
	float z = 2.0 * f * n / (zn * (f - n) - (f + n));
	return vec3(ndc.x * -z * p.cam.z * p.cam.w, ndc.y * -z * p.cam.z, z);
}

float tile_hash(ivec3 t) {
	uint h = uint(t.x) * 73856093u ^ uint(t.y) * 19349663u ^ uint(t.z) * 83492791u;
	h ^= h >> 13u; h *= 1274126177u; h ^= h >> 16u;
	return float(h & 1023u) / 1024.0;
}

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	if (px.x >= int(p.info.x) || px.y >= int(p.info.y)) return;
	float d = texelFetch(depth_tex, px, 0).r;
	bool sky = p.info.z > 0.5 ? d <= 1e-7 : d >= 0.9999999;
	if (sky) { imageStore(ids_out, px, vec4(0.0)); return; }
	vec4 nr = texelFetch(nr_tex, px, 0);
	// Raw buffer: roughness * 127/255, flipped (1 - w) for dynamic objects; ids are roughness * 127, so w * 255.
	float a = nr.a;
	if (a > 0.5) a = 1.0 - a;
	float id = floor(a * 255.0 + 0.5);
	vec2 uv = (vec2(px) + 0.5) / p.info.xy;
	vec3 view = view_pos(uv, d);
	vec3 n = normalize(nr.xyz * 2.0 - 1.0);
	vec3 world = (p.inv_view * vec4(view - n * 0.05, 1.0)).xyz;
	imageStore(ids_out, px, vec4(id / 255.0, -view.z, tile_hash(ivec3(floor(world))), 1.0));
}";

	RenderingDevice? _rd;
	Rid _shader, _pipeline, _sampler, _params;
	Rid _out;
	Vector2I _size;
	readonly float[] _paramData = new float[24];
	readonly byte[] _paramBytes = new byte[24 * 4];
	/// <summary>Errors once (a missing buffer), not every frame.</summary>
	bool _warned;

	/// <summary>The ids image (RD texture) for the present shader, sized with <see cref="Resize"/>.</summary>
	public Texture2Drd Texture { get; } = new();

	public IdsEffect()
	{
		EffectCallbackType = EffectCallbackTypeEnum.PostOpaque;
		NeedsNormalRoughness = true;
		_rd = RenderingServer.GetRenderingDevice();
		if (_rd == null) return;
		var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = Source };
		var spirv = _rd.ShaderCompileSpirVFromSource(src);
		if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) { GD.PushError("IdsEffect: " + spirv.CompileErrorCompute); _rd = null; return; }
		_shader = _rd.ShaderCreateFromSpirV(spirv);
		_pipeline = _rd.ComputePipelineCreate(_shader);
		_sampler = _rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest });
		_params = _rd.UniformBufferCreate((uint)_paramBytes.Length);
	}

	public bool Ready => _rd != null;

	/// <summary>(Re)makes the output at the art size (main thread, before the frame renders).</summary>
	public void Resize(Vector2I size)
	{
		if (_rd == null || size == _size) return;
		_size = size;
		var fmt = new RDTextureFormat
		{
			Width = (uint)size.X, Height = (uint)size.Y, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit |
			            RenderingDevice.TextureUsageBits.CanCopyFromBit,
		};
		var old = _out;
		_out = _rd.TextureCreate(fmt, new RDTextureView());
		Texture.TextureRdRid = _out;
		if (old.IsValid) _rd.FreeRid(old);
	}

	/// <summary>The camera this frame (main thread): its projection as Godot renders it, and its transform.</summary>
	public void SetCamera(Camera3D cam, Transform3D camera, bool reverseZ)
	{
		var b = camera.Basis;
		Put(0, new Vector4(b.Column0.X, b.Column0.Y, b.Column0.Z, 0)); Put(4, new Vector4(b.Column1.X, b.Column1.Y, b.Column1.Z, 0));
		Put(8, new Vector4(b.Column2.X, b.Column2.Y, b.Column2.Z, 0)); Put(12, new Vector4(camera.Origin.X, camera.Origin.Y, camera.Origin.Z, 1));
		bool ortho = cam.Projection == Camera3D.ProjectionType.Orthogonal;
		Put(16, new Vector4(_size.X, _size.Y, reverseZ ? 1 : 0, ortho ? 1 : 0));
		float half = ortho ? cam.Size * 0.5f : Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f); // KeepAspect Height: Size and Fov are vertical
		Put(20, new Vector4(cam.Near, cam.Far, half, _size.X / (float)Math.Max(1, _size.Y)));
		Buffer.BlockCopy(_paramData, 0, _paramBytes, 0, _paramBytes.Length);
	}

	void Put(int at, Vector4 v) { _paramData[at] = v.X; _paramData[at + 1] = v.Y; _paramData[at + 2] = v.Z; _paramData[at + 3] = v.W; }

	public override void _RenderCallback(int effectCallbackType, RenderData renderData)
	{
		if (_rd == null || !_out.IsValid) return;
		if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers) return;
		var size = buffers.GetInternalSize();
		if (size != _size) return; // resized this frame: the next one matches
		Rid depth = buffers.GetDepthLayer(0), nr = buffers.GetTexture("forward_clustered", "normal_roughness");
		if (!depth.IsValid || !nr.IsValid)
		{
			if (!_warned) GD.PushError("IdsEffect: no depth or normal-roughness buffer");
			_warned = true;
			return;
		}
		_rd.BufferUpdate(_params, 0, (uint)_paramBytes.Length, _paramBytes);
		var set = UniformSetCacheRD.GetCache(_shader, 0, new Godot.Collections.Array<RDUniform>
		{
			Uniform(RenderingDevice.UniformType.SamplerWithTexture, 0, _sampler, depth),
			Uniform(RenderingDevice.UniformType.SamplerWithTexture, 1, _sampler, nr),
			Uniform(RenderingDevice.UniformType.Image, 2, _out),
			Uniform(RenderingDevice.UniformType.UniformBuffer, 3, _params),
		});
		long list = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(list, _pipeline);
		_rd.ComputeListBindUniformSet(list, set, 0);
		_rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
		_rd.ComputeListEnd();
	}

	static RDUniform Uniform(RenderingDevice.UniformType type, int binding, params Rid[] ids)
	{
		var u = new RDUniform { UniformType = type, Binding = binding };
		foreach (var id in ids) u.AddId(id);
		return u;
	}

	/// <summary>The ids image read back (diagnostics; stalls the GPU).</summary>
	public Image? Read()
	{
		if (_rd == null || !_out.IsValid) return null;
		var bytes = _rd.TextureGetData(_out, 0);
		return Image.CreateFromData(_size.X, _size.Y, false, Image.Format.Rgbah, bytes);
	}

	/// <summary>Frees the GPU objects (the renderer calls it as it leaves the tree; resources aren't finalized in time).</summary>
	public void Release()
	{
		if (_rd == null) return;
		Enabled = false;
		Texture.TextureRdRid = default;
		foreach (var rid in new[] { _out, _params, _sampler, _pipeline, _shader })
			if (rid.IsValid) _rd.FreeRid(rid);
		_out = _params = _sampler = _pipeline = _shader = default;
		_rd = null;
	}
}
