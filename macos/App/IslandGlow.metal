#include <metal_stdlib>
using namespace metal;

// Native GPU evaluation of Beta 7's finite exterior light field. The body
// contour never moves. Output is premultiplied, with a transparent outer edge.
static float gaussian(float x) { return abs(x) >= 8 ? 0 : exp(-0.5 * x * x); }
static float weight(float p, float center, float width) {
    float d = abs(fract(p) - fract(center));
    return gaussian(min(d, 1 - d) / width);
}
static float3 palette(float p, float3 centers) {
    float3 w = float3(weight(p, centers.x, .14), weight(p, centers.y, .16), weight(p, centers.z, .17));
    return (float3(1, 147.0/255, 78.0/255) * w.x +
            float3(1, 79.0/255, 171.0/255) * w.y +
            float3(69.0/255, 137.0/255, 1) * w.z) / max(1e-10, w.x + w.y + w.z);
}
static void composite(thread float4 &value, float3 color, float opacity) {
    value = float4(color * opacity, opacity) + value * (1 - opacity);
}
static float4 light(float around, float distance, float phase, float brightness,
                    device const float *bands, int count) {
    if (distance <= -3 || distance >= 17) return 0;
    float bandPosition = fract(around) * 6;
    int index = int(bandPosition);
    float blend = smoothstep(0.0, 1.0, fract(bandPosition));
    float peak = 0;
    for (int i = 0; i < min(6, count); i++) peak = max(peak, clamp(bands[i], 0.0, 1.0));
    float a = index < count ? bands[index] : 0;
    float b = (index + 1) % 6 < count ? bands[(index + 1) % 6] : 0;
    float motion = sqrt(clamp(.55 * mix(a, b, blend) + .45 * peak, 0.0, 1.0));
    float theta = around * 2 * M_PI_F;
    float crest = .5 + .5 * (.7 * sin(theta * 2 - phase * 1.1) + .3 * sin(theta * 3 - phase * .7));
    float localLight = .7 + .3 * motion;
    float strength = min(.95, 1.65 * brightness * localLight * (.2 + .8 * motion) * (.65 + .35 * motion * crest));
    float3 centers = float3(.08, .42, .76) + phase * .095 +
        float3(.035 * sin(phase * .6), .045 * sin(phase * .55 + 2), .04 * sin(phase * .5 + 4));
    float3 waves = float3(.1 + phase * .07 + .1 * sin(phase * .35),
        .43 + phase * .08 + .085 * sin(phase * .41 + 2), .74 + phase * .055 + .09 * sin(phase * .3 + 4));
    float3 blooms = float3(.04 + phase * .045 + .07 * sin(phase * .28 + 1),
        .38 + phase * .05 + .08 * sin(phase * .32 + 3), .72 + phase * .04 + .07 * sin(phase * .26 + 5));
    float3 base = palette(around, centers);
    float4 value = 0;
    composite(value, palette(around - .08, blooms), .16 * brightness * localLight * gaussian(distance / (8.5 + 1.5 * motion)));
    composite(value, palette(around, blooms), .32 * brightness * localLight * (.35 + .65 * motion * crest) * gaussian(distance / (3.2 + 7 * motion * crest)));
    composite(value, palette(around, waves), strength * gaussian(distance / (1.25 + 10 * motion * crest)));
    composite(value, base, .95 * brightness * gaussian(distance / 1.6));
    composite(value, base * .88 + .12, .42 * brightness * gaussian(distance / .9));
    return value * smoothstep(0.0, 6.0, 17 - distance) * smoothstep(0.0, 1.5, distance + 3);
}
[[ stitchable ]] half4 islandGlow(float2 position, half4 source, float2 size, float radius,
    float phase, float brightness, float simplified, device const float *bands, int count) {
    if (brightness <= 0 || min(size.x, size.y) <= 0) return 0;
    float2 halfSize = size / 2;
    radius = clamp(radius, 0.0, min(halfSize.x, halfSize.y));
    float2 delta = position - 18 - halfSize;
    float2 q = abs(delta) - halfSize + radius;
    float distance = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
    if (distance <= -3 || distance >= 17) return 0;
    float quarter = halfSize.x + halfSize.y + (M_PI_F / 2 - 2) * radius;
    float perimeter = 4 * quarter;
    float arc = q.x > 0 && q.y > 0 ? halfSize.x - radius + radius * atan2(q.x, q.y) :
        q.x > q.y ? halfSize.x - radius + M_PI_F * radius / 2 + halfSize.y - radius - abs(delta.y) : abs(delta.x);
    arc = delta.y >= 0 ? (delta.x >= 0 ? arc : perimeter - arc) :
        (delta.x >= 0 ? 2 * quarter - arc : 2 * quarter + arc);
    float4 full = light(fract(arc / perimeter + .75), distance, phase, brightness, bands, count);
    if (simplified <= 0) return half4(full);
    // Project the lower tangent and the first45 degrees of each lower corner
    // downward, feathering its ends; never mask with a center-origin cone.
    float tangent = halfSize.x - radius;
    float endX = tangent + radius / sqrt(2.0);
    float sourceX = min(abs(delta.x), endX);
    float cornerX = max(0.0, sourceX - tangent);
    float sourceY = halfSize.y - radius + sqrt(max(0.0, radius * radius - cornerX * cornerX));
    float sourceArc = sourceX <= tangent || radius == 0 ? sourceX : tangent + radius * asin(cornerX / radius);
    float projected = delta.y - sourceY;
    float feather = max(1.0, M_PI_F * radius / 16);
    float coverage = smoothstep(0.0, feather, tangent + M_PI_F * radius / 4 + feather / 2 - sourceArc);
    coverage *= gaussian(max(0.0, abs(delta.x) - endX) / (1.8 + .45 * max(0.0, projected)));
    float projectedArc = delta.x >= 0 ? sourceArc : perimeter - sourceArc;
    float4 bottom = light(fract(projectedArc / perimeter + .75), projected, phase, brightness, bands, count) * coverage;
    return half4(mix(full, bottom, clamp(simplified, 0.0, 1.0)));
}
