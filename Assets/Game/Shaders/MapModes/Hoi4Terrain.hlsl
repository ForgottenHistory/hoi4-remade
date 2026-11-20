// Hoi4Terrain.hlsl
// GAME: Clausewitz-style terrain surface for converted HOI4 map data.
//
// Technique (same idea as HOI4's pdxmap terrain pass):
//   - terrain index map: one atlas tile (0-15) per map pixel, 255 = water
//   - 4x4 tile atlas (diffuse+gloss, normal+spec), manual LOD, 4 neighbour tiles blended bilinearly
//   - colormap overlay blend tints the tiles per map pixel
//   - world normal rotated by the tile normal, sun + ambient + Blinn-Phong spec
//   - water: colormap_water tint over a tiled sea floor, faded by depth, fresnel + sun glint
//
// All inputs are GAME globals set from map/visual_style.json5 (textures + tuning).
// Textures loaded there (Texture2D.LoadImage) have row 0 = bottom, so map-space textures
// are sampled with the raw mesh UV (north = v 1). Engine simulation textures use 1 - v.

#ifndef HOI4_TERRAIN_INCLUDED
#define HOI4_TERRAIN_INCLUDED

#define HOI4_WATER_TILE 255

TEXTURE2D(_Hoi4TerrainIndex);
TEXTURE2D(_Hoi4AtlasDiffuse);
TEXTURE2D(_Hoi4AtlasNormal);
TEXTURE2D(_Hoi4Colormap);
TEXTURE2D(_Hoi4ColormapWater);
TEXTURE2D(_Hoi4Underwater);
TEXTURE2D(_Hoi4WorldNormal);
// D3D11 allows 16 samplers per pixel shader and the engine already uses most of them,
// so all HOI4 textures share two inline sampler states (style json5 filter/wrap are ignored).
SAMPLER(sampler_trilinear_clamp);
SAMPLER(sampler_trilinear_repeat);

float  _Hoi4TileFrequency;      // atlas tile repeats over map height (HOI4: 128)
float  _Hoi4ColormapStrength;   // HOI4: 0.75
float  _Hoi4PoliticalBlend;     // country color over terrain
float  _Hoi4SunIntensity;
float4 _Hoi4SunColor;
float4 _Hoi4SunDirection;       // direction TOWARDS the sun (xyz)
float4 _Hoi4AmbientColor;
float  _Hoi4SpecularStrength;
float  _Hoi4WaterLightness;     // HOI4: 0.5
float4 _Hoi4WaterReflection;    // stand-in for the sky cubemap
float  _Hoi4WaterReflectionStrength;
float  _Hoi4WaterDarken;        // HOI4: 0.2
float  _Hoi4WaterFloorVisibility; // how much lit sea floor shows in shallow water (0-1)
float  _Hoi4WaterFloorRelief;   // how much bathymetry relief lights the sea floor (0 flat, 1 full)
float4 _Hoi4WaterSurfaceColor;  // surface color laid over the water
float  _Hoi4WaterSurfaceOpacity;// surface color strength in deep water (shallow water gets less)

// Unset globals read 0: fall back to HOI4 defaults so the map never renders black
#define HOI4_OR(v, d) ((v) > 0.0 ? (v) : (d))

struct Hoi4Surface
{
    float3 albedo;     // land diffuse (before lighting)
    float3 normal;     // world space, y up
    float gloss;
    float spec;
    float land;        // 0 = water, 1 = land (bilinear coverage, gives soft 1px coasts)
};

// ----------------------------------------------------------------------------
// Atlas
// ----------------------------------------------------------------------------

// HOI4 sample_terrain(): tile (u,v) in a 4x4 atlas, inset by one texel at the current mip
// so bilinear filtering never bleeds into the neighbour tile. Computed top-down (like DX),
// then flipped for the bottom-up loaded PNG.
float2 Hoi4AtlasUV(uint tile, float2 tileRepeat, float mipTexels)
{
    float2 index = float2(tile % 4, tile / 4);
    float2 f = frac(tileRepeat);
    float texelsPerTile = mipTexels / 4.0;
    f *= (texelsPerTile - 1.0) / texelsPerTile;
    float2 uv = (index + f) / 4.0 + 0.5 / mipTexels;
    return float2(uv.x, 1.0 - uv.y);
}

void Hoi4SampleTile(uint tile, float2 tileRepeat, float mipTexels, float lod, out float4 diffuse, out float4 normal)
{
    float2 auv = Hoi4AtlasUV(tile, tileRepeat, mipTexels);
    diffuse = SAMPLE_TEXTURE2D_LOD(_Hoi4AtlasDiffuse, sampler_trilinear_clamp, auv, lod);
    normal = SAMPLE_TEXTURE2D_LOD(_Hoi4AtlasNormal, sampler_trilinear_clamp, auv, lod);
}

// HOI4 RotateVectorByVector: bend the world normal by a tangent-space tile normal
float3 Hoi4RotateNormal(float3 n, float3 t)
{
    float3 xaxis = normalize(cross(n, float3(0, 0, 1)));
    float3 yaxis = normalize(cross(xaxis, n));
    return xaxis * t.x + n * t.y + yaxis * t.z;
}

// HOI4 GetOverlay: overlay blend in gamma space
float3 Hoi4Overlay(float3 color, float3 overlay, float strength)
{
    float3 c = LinearToSRGB(color);
    float3 o = LinearToSRGB(overlay);
    float3 res = o < 0.5 ? (2.0 * o * c) : (1.0 - 2.0 * (1.0 - o) * (1.0 - c));
    return lerp(color, SRGBToLinear(res), strength);
}

// ----------------------------------------------------------------------------
// Surface
// ----------------------------------------------------------------------------

// Land coverage 0..1 at uv: the same bilinear terrain-index weights Hoi4SampleSurface uses for
// s.land, without sampling the atlas. For effects that must stay off water (city lights: HOI4's
// emissive mask is soft and spills into the sea, where HOI4's water surface hides it).
float Hoi4LandFraction(float2 uv)
{
    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);
    float2 p = uv * float2(w, h) - 0.5;
    int2 i0 = (int2)floor(p);
    float2 f = p - floor(p);
    int2 maxI = int2(w - 1, h - 1);

    float land = 0;
    [unroll]
    for (int k = 0; k < 4; k++)
    {
        int2 o = int2(k & 1, k >> 1);
        uint tile = (uint)(LOAD_TEXTURE2D(_Hoi4TerrainIndex, clamp(i0 + o, int2(0, 0), maxI)).r * 255.0 + 0.5);
        float weight = (o.x ? f.x : 1 - f.x) * (o.y ? f.y : 1 - f.y);
        if (tile != HOI4_WATER_TILE)
            land += weight;
    }
    return land;
}

Hoi4Surface Hoi4SampleSurface(float2 uv)
{
    Hoi4Surface s;

    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);
    float2 size = float2(w, h);

    // 4 neighbour pixels around this point (HOI4: uv - half texel, frac(uv*size - 0.5))
    float2 p = uv * size - 0.5;
    int2 i0 = (int2)floor(p);
    float2 f = p - floor(p);
    int2 maxI = int2(w - 1, h - 1);

    uint tiles[4];
    tiles[0] = (uint)(LOAD_TEXTURE2D(_Hoi4TerrainIndex, clamp(i0,               int2(0, 0), maxI)).r * 255.0 + 0.5);
    tiles[1] = (uint)(LOAD_TEXTURE2D(_Hoi4TerrainIndex, clamp(i0 + int2(1, 0),  int2(0, 0), maxI)).r * 255.0 + 0.5);
    tiles[2] = (uint)(LOAD_TEXTURE2D(_Hoi4TerrainIndex, clamp(i0 + int2(0, 1),  int2(0, 0), maxI)).r * 255.0 + 0.5);
    tiles[3] = (uint)(LOAD_TEXTURE2D(_Hoi4TerrainIndex, clamp(i0 + int2(1, 1),  int2(0, 0), maxI)).r * 255.0 + 0.5);

    float weights[4];
    weights[0] = (1 - f.x) * (1 - f.y);
    weights[1] = f.x * (1 - f.y);
    weights[2] = (1 - f.x) * f.y;
    weights[3] = f.x * f.y;

    // Tile repeat in top-down orientation (matches HOI4 uv2), aspect corrected
    float2 tileRepeat = float2(uv.x * size.x / size.y, 1.0 - uv.y) * HOI4_OR(_Hoi4TileFrequency, 128.0);

    // Manual LOD (HOI4 mipmapLevel(): 512 texels per tile, biased -0.5, clamped 0-6)
    float2 dx = ddx(tileRepeat * 512.0);
    float2 dy = ddy(tileRepeat * 512.0);
    float lod = clamp(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) - 0.5, 0.0, 6.0);
    float mipTexels = exp2(11.0 - lod);

    float4 diffuse = 0;
    float4 normalRaw = 0;
    float land = 0;

    bool allSame = tiles[0] == tiles[1] && tiles[0] == tiles[2] && tiles[0] == tiles[3];
    if (allSame)
    {
        if (tiles[0] != HOI4_WATER_TILE)
        {
            Hoi4SampleTile(tiles[0], tileRepeat, mipTexels, lod, diffuse, normalRaw);
            land = 1;
        }
    }
    else
    {
        [unroll]
        for (int k = 0; k < 4; k++)
        {
            if (tiles[k] != HOI4_WATER_TILE)
            {
                float4 d, n;
                Hoi4SampleTile(tiles[k], tileRepeat, mipTexels, lod, d, n);
                diffuse += d * weights[k];
                normalRaw += n * weights[k];
                land += weights[k];
            }
        }
        if (land > 0)
        {
            diffuse /= land;
            normalRaw /= land;
        }
    }

    // Derivative-based samples stay outside the branches below
    // World normal (HOI4 HeightNormal: .rbg - 0.5, y up)
    float3 worldNormal = normalize(SAMPLE_TEXTURE2D(_Hoi4WorldNormal, sampler_trilinear_clamp, uv).rbg - 0.5);
    float3 tint = SAMPLE_TEXTURE2D(_Hoi4Colormap, sampler_trilinear_clamp, uv).rgb;

    if (land > 0)
    {
        float3 tileNormal = normalize(normalRaw.rbg - 0.5);
        s.normal = normalize(Hoi4RotateNormal(worldNormal, tileNormal));

        s.albedo = Hoi4Overlay(diffuse.rgb, tint, HOI4_OR(_Hoi4ColormapStrength, 0.75));
        s.gloss = diffuse.a;
        s.spec = normalRaw.a;
    }
    else
    {
        s.normal = worldNormal;
        s.albedo = 0;
        s.gloss = 0;
        s.spec = 0;
    }
    s.land = land;
    return s;
}

// ----------------------------------------------------------------------------
// Lighting
// ----------------------------------------------------------------------------

float3 Hoi4SunDir()
{
    float3 d = _Hoi4SunDirection.xyz;
    return dot(d, d) > 0 ? normalize(d) : normalize(float3(1.0, 1.0, -0.5)); // HOI4 LIGHT_DIRECTION (-1,-1,0.5), reversed
}

float3 Hoi4Light(float3 albedo, float3 normal, float3 positionWS, float gloss, float spec)
{
    float3 L = Hoi4SunDir();
    float3 V = normalize(GetCameraPositionWS() - positionWS);
    float3 H = normalize(L + V);

    float3 sun = (_Hoi4SunColor.a > 0 ? _Hoi4SunColor.rgb : float3(1, 1, 1)) * HOI4_OR(_Hoi4SunIntensity, 0.8);
    float3 ambient = _Hoi4AmbientColor.a > 0 ? _Hoi4AmbientColor.rgb : float3(0.42, 0.45, 0.5);

    float ndl = saturate(dot(normal, L));
    float specPower = lerp(8.0, 96.0, gloss);
    float specular = pow(saturate(dot(normal, H)), specPower) * spec * _Hoi4SpecularStrength;

    return albedo * (ambient + sun * ndl) + sun * specular;
}

// ----------------------------------------------------------------------------
// Water
// ----------------------------------------------------------------------------

float3 Hoi4Water(float2 uv, float3 positionWS, float3 worldNormal)
{
    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);

    // HOI4 underwater pass: tiled sea floor, faded to the water tint with depth.
    // Heights in HOI4 units (heightmap 0-255 * 0.1; sea level 9.5).
    float height = SAMPLE_TEXTURE2D(_HeightmapTexture, sampler_HeightmapTexture, float2(uv.x, 1.0 - uv.y)).r * 25.5;
    float shallow = saturate((height - 5.3) / (13.0 - 5.3));

    float3 tint = SAMPLE_TEXTURE2D(_Hoi4ColormapWater, sampler_trilinear_clamp, uv).rgb * HOI4_OR(_Hoi4WaterLightness, 0.5);
    float2 floorUV = float2(uv.x, 1.0 - uv.y) * float2(w / 32.0, h / 32.0);
    float3 seaFloor = SAMPLE_TEXTURE2D(_Hoi4Underwater, sampler_trilinear_repeat, floorUV).rgb;
    // Flatten the bathymetry relief: HOI4's sea reads as a calm surface, not a lit sea floor
    float3 floorNormal = normalize(lerp(float3(0, 1, 0), worldNormal, saturate(_Hoi4WaterFloorRelief)));
    seaFloor = Hoi4Light(seaFloor, floorNormal, positionWS, 0, 0);

    float floorAmount = shallow * saturate(_Hoi4WaterFloorVisibility);
    float3 color = lerp(tint, seaFloor, floorAmount);

    // Surface color, strongest over deep water
    if (_Hoi4WaterSurfaceColor.a > 0)
        color = lerp(color, _Hoi4WaterSurfaceColor.rgb, saturate(_Hoi4WaterSurfaceOpacity) * (1.0 - floorAmount));

    // Surface: fresnel towards the sky color, plus a sun glint on a flat surface
    float3 V = normalize(GetCameraPositionWS() - positionWS);
    float fresnel = pow(1.0 - saturate(V.y), 5.0);
    float3 sky = _Hoi4WaterReflection.a > 0 ? _Hoi4WaterReflection.rgb : float3(0.25, 0.32, 0.4);
    color = lerp(color, sky, saturate(fresnel + _Hoi4WaterReflectionStrength));

    float3 H = normalize(Hoi4SunDir() + V);
    color += pow(saturate(H.y), 256.0) * _Hoi4SpecularStrength * 0.5;

    return lerp(color, 0, _Hoi4WaterDarken);
}

// Full lit HOI4 pixel. countryColor.a = how much country color to paint over the land.
float4 Hoi4Render(float2 uv, float3 positionWS, float4 countryColor)
{
    Hoi4Surface s = Hoi4SampleSurface(uv);

    // Water uses derivative-based samples, so it is not branched on s.land
    float3 water = Hoi4Water(uv, positionWS, s.normal);

    float3 land = 0;
    if (s.land > 0)
    {
        float3 albedo = lerp(s.albedo, countryColor.rgb, countryColor.a);
        land = Hoi4Light(albedo, s.normal, positionWS, s.gloss, s.spec * (1 - countryColor.a));
    }

    return float4(lerp(water, land, s.land), 1);
}

#endif // HOI4_TERRAIN_INCLUDED
