#pragma once
#include <algorithm>
#include <cstdint>
#include <optional>
#include <string_view>
namespace dropspace {
constexpr uint64_t model_bytes = 1908528192;
constexpr uint64_t gib = 1024ULL * 1024 * 1024;
enum class model_profile { hy_mt2_1_8b_q8, hy_mt2_7b_q8 };
struct model_policy {
    std::string_view id;
    uint64_t bytes;
    uint64_t gpu_allowance;
    uint64_t host_allowance;
};
inline constexpr model_policy hy_1_8b_policy{"hy-mt2-1.8b-q8", model_bytes, gib, 3 * gib};
inline constexpr model_policy hy_7b_policy{"hy-mt2-7b-q8", 7981928896ULL, 2 * gib, 4 * gib};

inline constexpr const model_policy * policy_for(model_profile profile) {
    switch (profile) {
        case model_profile::hy_mt2_1_8b_q8: return &hy_1_8b_policy;
        case model_profile::hy_mt2_7b_q8: return &hy_7b_policy;
    }
    return nullptr;
}
inline std::optional<model_profile> parse_model_profile(std::string_view id) {
    if (id == hy_1_8b_policy.id) return model_profile::hy_mt2_1_8b_q8;
    if (id == hy_7b_policy.id) return model_profile::hy_mt2_7b_q8;
    return std::nullopt;
}
inline bool model_size_matches(model_profile profile, uint64_t actual_bytes) {
    const auto policy = policy_for(profile);
    return policy && actual_bytes == policy->bytes;
}
// Actual vendor-specific kernels are selected by pinned ggml from reported shader,
// subgroup/cooperative-matrix capabilities, never a product-name/brand heuristic.
inline bool gpu_fits(uint32_t vendor, bool discrete, bool memory_budget,
                     uint64_t free, uint64_t total, uint64_t host_available = 0,
                     model_profile profile = model_profile::hy_mt2_1_8b_q8) {
    const auto policy = policy_for(profile);
    if (!policy || (vendor != 0x10de && vendor != 0x1002) || !memory_budget || free > total) return false;
    const auto reserve = std::max(gib, total / 5); // preserve >=20% for desktop/video, >=1 GiB
    // Integrated GPUs must also have a separately measured OS available-RAM budget;
    // a reported shared-memory capacity by itself is not available memory.
    if (!discrete && host_available < policy->bytes + policy->host_allowance) return false;
    return total >= 4 * gib && free >= policy->bytes + policy->gpu_allowance + reserve;
}
}
