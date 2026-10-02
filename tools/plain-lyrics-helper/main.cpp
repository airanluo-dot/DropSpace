// Private inherited-pipe worker. No socket, URL, download, config file or interactive console.
#include "arg.h"
#include "chat.h"
#include "common.h"
#include "log.h"
#include "sampling.h"
#include "gpu-policy.h"
#include <filesystem>
#include <iostream>
#include <memory>
#include <stdexcept>
#ifdef DROPSPACE_VULKAN
#include <vulkan/vulkan.h>
#ifdef _WIN32
#include <windows.h>
#endif
#endif
using json = common_json;
constexpr size_t max_frame = 32768;

static bool read_frame(std::string & out) {
    out.clear();
    char c;
    while (std::cin.get(c)) {
        if (c == '\n') return true;
        if (out.size() >= max_frame) throw std::runtime_error("frame-budget");
        out += c;
    }
    if (!out.empty()) throw std::runtime_error("incomplete-frame");
    return false;
}
static void send(const json & value) { std::cout << value.dump() << '\n' << std::flush; }

#ifdef DROPSPACE_VULKAN
static ggml_backend_dev_t choose_gpu(json & selected_device, dropspace::model_profile model_profile) {
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO}; app.apiVersion = VK_API_VERSION_1_2;
    VkInstanceCreateInfo info{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO}; info.pApplicationInfo = &app;
    VkInstance instance{};
    if (vkCreateInstance(&info, nullptr, &instance) != VK_SUCCESS) return nullptr;
    struct owner { VkInstance i; ~owner(){vkDestroyInstance(i, nullptr);} } owned{instance};
    uint32_t count = 0;
    if (vkEnumeratePhysicalDevices(instance, &count, nullptr) != VK_SUCCESS || count > 32) return nullptr;
    std::vector<VkPhysicalDevice> physical(count);
    if (vkEnumeratePhysicalDevices(instance, &count, physical.data()) != VK_SUCCESS) return nullptr;
    auto reg = ggml_backend_reg_by_name("Vulkan");
    if (!reg) return nullptr;
    ggml_backend_dev_t best = nullptr;
    uint64_t best_free = 0;
    bool best_discrete = false;
    uint64_t host_available = 0;
#ifdef _WIN32
    MEMORYSTATUSEX memory{}; memory.dwLength = sizeof(memory);
    if (GlobalMemoryStatusEx(&memory)) host_available = memory.ullAvailPhys;
#endif
    for (size_t i = 0; i < ggml_backend_reg_dev_count(reg); ++i) {
        auto device = ggml_backend_reg_dev_get(reg, i);
        ggml_backend_dev_props props{}; ggml_backend_dev_get_props(device, &props);
        if (props.type != GGML_BACKEND_DEVICE_TYPE_GPU && props.type != GGML_BACKEND_DEVICE_TYPE_IGPU) continue;
        for (auto vk : physical) {
            VkPhysicalDeviceProperties p{}; vkGetPhysicalDeviceProperties(vk, &p);
            if (std::string(p.deviceName) != props.description) continue;
            uint32_t n = 0;
            if (vkEnumerateDeviceExtensionProperties(vk, nullptr, &n, nullptr) != VK_SUCCESS || n > 4096) continue;
            std::vector<VkExtensionProperties> extensions(n);
            if (vkEnumerateDeviceExtensionProperties(vk, nullptr, &n, extensions.data()) != VK_SUCCESS) continue;
            bool budget = false;
            for (const auto & extension : extensions)
                if (std::string(extension.extensionName) == "VK_EXT_memory_budget") budget = true;
            const bool discrete = p.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU;
            if (dropspace::gpu_fits(p.vendorID, discrete, budget, props.memory_free, props.memory_total, host_available, model_profile) &&
                (!best || (discrete && !best_discrete) || (discrete == best_discrete && props.memory_free > best_free))) {
                best = device; best_free = props.memory_free; best_discrete = discrete;
                selected_device = {{"vendorId", p.vendorID}, {"name", props.description}, {"discrete", discrete},
                    {"memoryFreeBytes", props.memory_free}, {"memoryTotalBytes", props.memory_total}};
            }
        }
    }
    return best;
}
#endif

int main(int argc, char ** argv) {
    try {
        if (argc == 2 && std::string(argv[1]) == "--version") {
            send({{"protocol", 1}, {"profile", "hy-q8-plain-resident-v1"}}); return 0;
        }
        if ((argc != 5 && argc != 7) || std::string(argv[1]) != "--model" || std::string(argv[3]) != "--mode") return 64;
        auto model_profile = dropspace::model_profile::hy_mt2_1_8b_q8;
        if (argc == 7) {
            if (std::string(argv[5]) != "--model-profile") return 64;
            const auto requested = dropspace::parse_model_profile(argv[6]);
            if (!requested) return 64;
            model_profile = *requested;
        }
        const std::string mode = argv[4];
        if (mode != "cpu" && mode != "vulkan") return 64;
#ifndef DROPSPACE_VULKAN
        if (mode != "cpu") return 65;
#endif
        // The host verifies the pinned digest under a retained file lease. This exact
        // size check also prevents a selected profile from understating its budget.
        std::error_code model_error;
        const auto actual_bytes = std::filesystem::file_size(std::filesystem::u8path(argv[2]), model_error);
        if (model_error || !dropspace::model_size_matches(model_profile, actual_bytes)) return 67;
        // Use pinned upstream argument parsing to freeze defaults, explicit sampler flags and
        // model-metadata override bits exactly like the evaluated llama-completion command.
        std::vector<std::string> args = {"plain-lyrics-worker", "-m", argv[2], "--offline", "--perf", "--no-escape",
            "--jinja", "--single-turn", "--load-mode", "none", "--no-display-prompt", "--simple-io",
            "--no-context-shift", "--reasoning", "off", "-t", "4", "-tb", "4", "-ngl", "0", "-c", "4096", "-n", "2048",
            "--seed", "42", "--temp", "0.1", "--top-k", "20", "--top-p", "0.8", "--min-p", "0.05",
            "--repeat-penalty", "1.0", "--frequency-penalty", "0", "--presence-penalty", "0"};
        std::vector<char *> argv_fixed;
        for (auto & arg : args) argv_fixed.push_back(arg.data());
#if defined(DROPSPACE_VULKAN) && defined(_WIN32)
        if (!LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32)) return 65;
#endif
        common_params params;
        common_init();
        common_log_set_verbosity_thold(-1);
        llama_log_set([](ggml_log_level, const char *, void *){}, nullptr);
        if (!common_params_parse(static_cast<int>(argv_fixed.size()), argv_fixed.data(), params, LLAMA_EXAMPLE_COMPLETION)) return 64;
        // Prevent fitting from silently changing the frozen context or selecting other devices.
        params.fit_params = false;
        params.split_mode = LLAMA_SPLIT_MODE_NONE;
        params.devices = {nullptr};
        llama_backend_init();
#ifdef DROPSPACE_VULKAN
        json selected_device;
        if (mode == "vulkan") {
            auto device = choose_gpu(selected_device, model_profile);
            if (!device) return 66; // host will fully drain us before CPU fallback
            params.devices = {device, nullptr};
            params.n_gpu_layers = 999;
        }
#endif
        auto loaded = common_init_from_params(params);
        auto model = loaded->model(); auto ctx = loaded->context();
        if (!model || !ctx) return 67;
        auto templates = common_chat_templates_init(model, params.chat_template);
        const bool chat = common_chat_templates_was_explicit(templates.get());
        auto vocab = llama_model_get_vocab(model);
        json ready = {{"protocol", 1}, {"ready", true}, {"backend", mode},
            {"modelProfile", std::string(dropspace::policy_for(model_profile)->id)}};
#ifdef DROPSPACE_VULKAN
        // Diagnostic identity comes from the selected physical adapter, never the
        // user's GPU preference. It does not expose prompt or model contents.
        if (mode == "vulkan") ready["device"] = selected_device;
#endif
        send(ready);
        std::string frame;
        while (read_frame(frame)) {
            const auto request = json::parse(frame);
            if (!request.is_object() || request.size() != 3 || request.at("protocol") != 1 ||
                !request.at("id").is_string() || !request.at("prompt").is_string()) return 68;
            const auto id = request.at("id").get<std::string>();
            const auto input = request.at("prompt").get<std::string>();
            if (id.size() != 32 || id.find_first_not_of("0123456789abcdef") != std::string::npos ||
                input.empty() || input.size() > 1800) return 68;
            // A new sampler restores seed and penalties. Clearing the complete memory erases
            // KV/recurrent state; each template application has a new single-message history.
            llama_memory_clear(llama_get_memory(ctx), true);
            struct clear_on_exit { llama_context * ctx; ~clear_on_exit(){llama_memory_clear(llama_get_memory(ctx), true);} } clear{ctx};
            auto sampling = params.sampling;
            std::unique_ptr<common_sampler, decltype(&common_sampler_free)> sampler(common_sampler_init(model, sampling), common_sampler_free);
            if (!sampler) return 69;
            std::string prompt = input;
            if (chat) {
                common_chat_templates_inputs inputs;
                inputs.use_jinja = params.use_jinja;
                common_chat_msg message; message.role = "user"; message.content = input;
                inputs.messages = {message}; inputs.add_generation_prompt = true;
                inputs.force_pure_content = params.force_pure_content_parser;
                // Deliberately matches pinned completion.cpp's initial-template application.
                prompt = common_chat_templates_apply(templates.get(), inputs).prompt;
            }
            auto tokens = common_tokenize(ctx, prompt, true, true);
            if (tokens.empty() || tokens.size() + 2048 >= 4096) return 70;
            for (auto token : tokens) common_sampler_accept(sampler.get(), token, false);
            for (size_t offset = 0; offset < tokens.size(); offset += params.n_batch) {
                const auto n = std::min<size_t>(params.n_batch, tokens.size() - offset);
                if (llama_decode(ctx, llama_batch_get_one(tokens.data() + offset, static_cast<int32_t>(n)))) return 71;
            }
            std::string output; bool complete = false;
            for (int i = 0; i < 2048; ++i) {
                auto token = common_sampler_sample(sampler.get(), ctx, -1);
                common_sampler_accept(sampler.get(), token, true);
                if (llama_vocab_is_eog(vocab, token)) { complete = true; break; }
                output += common_token_to_piece(ctx, token, true);
                if (output.size() > 16384) return 72;
                if (llama_decode(ctx, llama_batch_get_one(&token, 1))) return 71;
            }
            // Never present a token-cap truncation as a complete translation.
            send({{"protocol", 1}, {"id", id}, {"complete", complete}, {"text", output}});
        }
        return 0;
    } catch (const std::exception &) { return 73; } // never echo private prompt/output to diagnostics
}
