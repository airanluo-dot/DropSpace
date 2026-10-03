#include "gpu-policy.h"
#include <cassert>
#include <limits>
int main() {
    using namespace dropspace;
    constexpr auto small = model_profile::hy_mt2_1_8b_q8;
    constexpr auto large = model_profile::hy_mt2_7b_q8;
    constexpr auto invalid = static_cast<model_profile>(-1);
    assert(parse_model_profile("hy-mt2-1.8b-q8") == small);
    assert(parse_model_profile("hy-mt2-7b-q8") == large);
    for (const auto name : {"", "hy-mt2-7b", "HY-MT2-7B-Q8", "hy-mt2-7b-q8 ", "7981928896", "--model-bytes"})
        assert(!parse_model_profile(name));
    assert(policy_for(invalid) == nullptr);
    assert(model_size_matches(small, 1908528192ULL));
    assert(model_size_matches(large, 7981928896ULL));
    assert(!model_size_matches(small, 7981928896ULL));
    assert(!model_size_matches(large, 1908528192ULL));
    for (const auto profile : {small, large}) {
        const auto bytes = policy_for(profile)->bytes;
        for (const auto actual : {uint64_t{0}, bytes - 1, bytes + 1, std::numeric_limits<uint64_t>::max()})
            assert(!model_size_matches(profile, actual));
    }
    assert(!model_size_matches(invalid, 0));

    // The default call remains the original 1.8B policy.
    assert(gpu_fits(0x10de, true, true, 6*gib, 8*gib));
    assert(gpu_fits(0x1002, true, true, 6*gib, 8*gib));
    assert(!gpu_fits(0x1002, true, false, 6*gib, 8*gib));
    assert(!gpu_fits(0x10de, true, true, 2*gib, 8*gib));
    assert(!gpu_fits(0x10de, true, true, 9*gib, 8*gib));
    assert(!gpu_fits(0x8086, true, true, 6*gib, 8*gib));
    assert(!gpu_fits(0x1002, false, true, 6*gib, 8*gib));
    assert(!gpu_fits(0x1002, false, true, 6*gib, 8*gib, 3*gib));
    assert(gpu_fits(0x1002, false, true, 6*gib, 8*gib, 8*gib));
    for (const auto total : {4*gib - 1, 4*gib, 8*gib, 12*gib, 16*gib})
        for (const auto free : {uint64_t{0}, total / 2, total})
            assert(gpu_fits(0x10de, true, true, free, total) ==
                   gpu_fits(0x10de, true, true, free, total, 0, small));
    const auto small_floor = model_bytes + 2*gib; // 1 GiB compute + 1 GiB reserve
    assert(gpu_fits(0x10de, true, true, small_floor, 4*gib));
    assert(!gpu_fits(0x10de, true, true, small_floor - 1, 4*gib));
    assert(!gpu_fits(0x10de, true, true, small_floor, 4*gib - 1));

    // Test the exact free-memory and independently measured host-memory boundaries
    // for each profile and vendor. No memory is allocated and no GPU is exercised.
    for (const auto profile : {small, large}) {
        const auto policy = policy_for(profile);
        const auto total = 16*gib;
        const auto required = policy->bytes + policy->gpu_allowance + total / 5;
        const auto host = policy->bytes + policy->host_allowance;
        for (const auto vendor : {0x10deU, 0x1002U}) {
            assert(gpu_fits(vendor, true, true, required, total, 0, profile));
            assert(!gpu_fits(vendor, true, true, required - 1, total, 0, profile));
            assert(!gpu_fits(vendor, true, false, required, total, host, profile));
            assert(!gpu_fits(vendor, true, true, total + 1, total, host, profile));
            assert(!gpu_fits(vendor, false, true, required, total, 0, profile));
            assert(!gpu_fits(vendor, false, true, required, total, host - 1, profile));
            assert(gpu_fits(vendor, false, true, required, total, host, profile));
            assert(!gpu_fits(vendor, false, true, required - 1, total, host, profile));
        }
        for (const auto vendor : {0U, 0x8086U, 0x1234U})
            assert(!gpu_fits(vendor, true, true, total, total, host, profile));
    }
    // A GPU that meets the legacy 1.8B threshold is not automatically 7B-capable.
    assert(!gpu_fits(0x10de, true, true, 8*gib, 8*gib, 32*gib, large));
    assert(!gpu_fits(0x1002, false, true, 16*gib, 16*gib, 8*gib, large));
    assert(!gpu_fits(0x10de, true, true, 16*gib, 16*gib, 32*gib, invalid));
}
