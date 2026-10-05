#pragma once
#include "gpu-policy.h"
#include <cuda_runtime_api.h>
// Called after pinned ggml registration. The device ordinal is read from ggml's
// CUDA<N> identity, never a Vulkan index, product-name match or user-supplied index.
static ggml_backend_dev_t choose_gpu(json & selected_device, dropspace::model_profile profile) {
    auto reg = ggml_backend_reg_by_name("CUDA");
    if (!reg) return nullptr;
    ggml_backend_dev_t best = nullptr;
    uint64_t best_free = 0;
    for (size_t i = 0; i < ggml_backend_reg_dev_count(reg); ++i) {
        auto device = ggml_backend_reg_dev_get(reg, i);
        ggml_backend_dev_props props{}; ggml_backend_dev_get_props(device, &props);
        const std::string name = props.name ? props.name : "";
        if (props.type != GGML_BACKEND_DEVICE_TYPE_GPU || name.rfind("CUDA", 0) != 0) continue;
        const auto number = name.substr(4);
        if (number.empty() || number.size() > 2 || number.find_first_not_of("0123456789") != std::string::npos) continue;
        const int ordinal = std::stoi(number);
        cudaDeviceProp cuda_props{};
        if (cudaGetDeviceProperties(&cuda_props, ordinal) != cudaSuccess || cuda_props.integrated ||
            cuda_props.major < 7 || (cuda_props.major == 7 && cuda_props.minor < 5) ||
            cudaSetDevice(ordinal) != cudaSuccess) continue;
        size_t free = 0, total = 0;
        if (cudaMemGetInfo(&free, &total) != cudaSuccess ||
            !dropspace::gpu_fits(0x10de, true, true, free, total, 0, profile)) continue;
        if (!best || free > best_free) {
            best = device; best_free = free;
            selected_device = {{"vendorId", 0x10de}, {"name", props.description}, {"discrete", true},
                {"ordinal", ordinal}, {"computeMajor", cuda_props.major}, {"computeMinor", cuda_props.minor},
                {"memoryFreeBytes", free}, {"memoryTotalBytes", total}};
        }
    }
    return best;
}
