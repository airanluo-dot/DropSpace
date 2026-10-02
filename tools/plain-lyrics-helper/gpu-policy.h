#pragma once
#include <algorithm>
#include <cstdint>
namespace dropspace {
constexpr uint64_t model_bytes = 1908528192;
constexpr uint64_t gib = 1024ULL * 1024 * 1024;
// Actual vendor-specific kernels are selected by pinned ggml from reported shader,
// subgroup/cooperative-matrix capabilities, never a product-name/brand heuristic.
inline bool gpu_fits(uint32_t vendor, bool discrete, bool memory_budget,
                     uint64_t free, uint64_t total, uint64_t host_available = 0) {
    if ((vendor != 0x10de && vendor != 0x1002) || !memory_budget || free > total) return false;
    const auto reserve = std::max(gib, total / 5); // preserve >=20% for desktop/video, >=1 GiB
    // Integrated GPUs must also have a separately measured OS available-RAM budget;
    // a reported shared-memory capacity by itself is not available memory.
    if (!discrete && host_available < model_bytes + 3 * gib) return false;
    return total >= 4 * gib && free >= model_bytes + gib + reserve;
}
}
