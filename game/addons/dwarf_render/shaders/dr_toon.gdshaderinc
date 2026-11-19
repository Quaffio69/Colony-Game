// =====================================================================
// Dwarf Render — shared toon lighting (included by every 3D shader).
// Port of the diorama's toonLighting(): banded sun, banded torches,
// optional rim light / hard specular glints / leaf back-light.
//
// Values are pushed to every registered material by DwarfRenderer
// (see DwarfMaterials.cs). Ambient light comes from your WorldEnvironment
// and is scaled per pixel through AO (sky exposure * vertex AO).
// =====================================================================

uniform float dr_bands = 3.0;      // number of flat shading bands
uniform float dr_torch = 1.2;      // multiplier for point/spot lights (torches, forges)
uniform float dr_rim = 0.0;        // cel rim light
uniform float dr_spec = 0.0;       // hard specular glints

// Set these in fragment(); light() reads them.
varying float v_sky;               // 0..1 sky exposure: 0 deep underground (no sun), 1 open sky
varying float v_transl;            // leaf back-light strength (0 for non-foliage)

float dr_band(float x) {
	return clamp(floor(x * dr_bands + 0.5) / max(dr_bands, 1.0), 0.0, 1.0);
}

vec3 dr_srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}
vec3 dr_linear_to_srgb(vec3 c) {
	c = max(c, vec3(0.0));
	return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

float dr_hash3(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

void light() {
	float ndl = max(dot(NORMAL, LIGHT), 0.0);
	if (LIGHT_IS_DIRECTIONAL) {
		// Sun / moon: banded N.L, shadows come in through ATTENUATION, fades out underground.
		float sky = smoothstep(0.02, 0.35, v_sky);
		DIFFUSE_LIGHT += LIGHT_COLOR / PI * dr_band(ndl * ATTENUATION) * sky;
		if (dr_rim > 0.0) {
			float rim = step(0.62, 1.0 - max(dot(NORMAL, VIEW), 0.0)) * (0.35 + 0.65 * ndl);
			DIFFUSE_LIGHT += LIGHT_COLOR / PI * rim * dr_rim * 0.7 * sky;
		}
		if (dr_spec > 0.0) {
			float sp = pow(max(dot(NORMAL, normalize(LIGHT + VIEW)), 0.0), 40.0);
			SPECULAR_LIGHT += LIGHT_COLOR / PI * step(0.55, sp) * ATTENUATION * dr_spec * 1.6 * sky;
		}
		if (v_transl > 0.0) {
			DIFFUSE_LIGHT += LIGHT_COLOR / PI * dr_band(max(dot(-NORMAL, LIGHT), 0.0) * v_transl) * 0.6 * sky;
		}
	} else {
		// Torches / forges: banded falloff gives stepped glow rings.
		float a = ATTENUATION * (ndl * 0.7 + 0.3) * 1.6;
		DIFFUSE_LIGHT += LIGHT_COLOR / PI * dr_band(a) * dr_torch;
	}
}
