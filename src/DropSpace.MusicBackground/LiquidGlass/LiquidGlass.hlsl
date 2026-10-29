// DropSpace liquid glass. Independently implemented for the existing island silhouette.
// All coordinates are physical pixels. The content visual is never sampled or filtered.
Texture2D Background : register(t0);
SamplerState LinearClamp : register(s0);
cbuffer Frame : register(b0)
{
    float4 Size;       // output width/height, source width/height
    float4 Shape;      // top radius, bottom radius, DPI, blur sigma
    float4 Optics;     // refraction multiplier, dispersion 0..1, dark multiplier, gradient
    float4 Crop;       // island origin in cropped background, blur direction
    float4 Lens;       // expanded refraction width multiplier; compact remains 1
};
struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Vertex VS(uint id : SV_VertexID)
{
    Vertex v;
    v.uv = float2((id << 1) & 2, id & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}
float4 Blur(Vertex v) : SV_Target
{
    if (Shape.w < 0.01) return Background.SampleLevel(LinearClamp, v.uv, 0);
    float2 stepUV = Crop.zw * max(Shape.w / 4.0, 0.25) / Size.zw;
    float4 sum = 0;
    float weightSum = 0;
    [unroll] for (int i = -12; i <= 12; ++i)
    {
        float w = exp(-0.5 * i * i / 16.0);
        sum += Background.SampleLevel(LinearClamp, v.uv + stepUV * i, 0) * w;
        weightSum += w;
    }
    return sum / weightSum;
}
float distanceToEdge(float2 p, out float2 normal)
{
    float2 halfSize = Size.xy * 0.5;
    float2 local = p - halfSize;
    float radius = p.y < halfSize.y ? Shape.x : Shape.y;
    radius = clamp(radius, 0, min(halfSize.x, halfSize.y));
    float2 q = abs(local) - halfSize + radius;
    float2 positive = max(q, 0);
    normal = length(positive) > 0.0001 ? normalize(positive) * sign(local)
        : (q.x > q.y ? float2(sign(local.x), 0) : float2(0, sign(local.y)));
    return length(positive) + min(max(q.x, q.y), 0) - radius;
}
float3 sampleBackground(float2 p) { return Background.SampleLevel(LinearClamp, (p + Crop.xy) / Size.zw, 0).rgb; }
float4 Glass(Vertex v) : SV_Target
{
    float2 p = v.uv * Size.xy;
    float2 direction;
    float d = distanceToEdge(p, direction);
    float alpha = saturate(0.5 - d);
    if (alpha <= 0) return 0;
    float depth = max(-d, 0);
    float bevel = max(1, min(20 * Shape.z, min(Size.x, Size.y) * 0.32));
    // Width changes the inward reach of the lens shoulder, independently of
    // displacement strength. Preserve the original bevel in the offset below.
    float influenceWidth = max(1, min(bevel * Lens.x, min(Size.x, Size.y) * 0.5));
    float t = saturate(depth / influenceWidth);
    // Rounded lens shoulder: Snell refraction falls continuously to zero at the flat center.
    float slope = (1 - t) * (1 - t) * 2.8;
    float3 normal = normalize(float3(direction * slope, 1));
    float3 ray = refract(float3(0, 0, -1), normal, 1.0 / 1.46);
    float2 offset = ray.xy / max(-ray.z, 0.01) * bevel * 0.8 * Optics.x;
    float spread = Optics.y * 0.5;
    float3 rgb = float3(sampleBackground(p + offset * (1 + spread)).r,
                        sampleBackground(p + offset).g,
                        sampleBackground(p + offset * (1 - spread)).b);
    float luminance = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    float shade = saturate((0.37 + 0.32 * smoothstep(0.35, 0.95, luminance)) * Optics.z);
    rgb = lerp(rgb, float3(0.039, 0.043, 0.059), shade);
    float black = 1 - smoothstep(0.15, 0.85, v.uv.y);
    rgb *= 1 - black * Optics.w;
    float highlight = pow(saturate(dot(direction, normalize(float2(-1, -1)))), 3)
                    + 0.42 * pow(saturate(dot(direction, normalize(float2(1, 1)))), 3);
    rgb += highlight * (1 - smoothstep(0, 1.5 * Shape.z, depth)) * 0.55;
    return float4(saturate(rgb) * alpha, alpha);
}
