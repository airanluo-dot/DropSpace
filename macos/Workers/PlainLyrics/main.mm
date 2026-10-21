// Private inherited-pipe worker. No socket, URL, download, config file or interactive console.
#include "arg.h"
#include "chat.h"
#include "common.h"
#include "log.h"
#include "sampling.h"
#include "gpu-policy.h"
#import <Metal/Metal.h>
#include <CommonCrypto/CommonDigest.h>
#include <mach/mach.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#include <algorithm>
#include <chrono>
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
using clock_type = std::chrono::steady_clock;

// Keep the exact verified file open through model loading and inference. The
// loader opens /dev/fd/N, so replacing the original pathname cannot substitute
// another model between verification and gguf parsing.
struct verified_translation_model {
    int fd = -1;
    explicit verified_translation_model(const char * path, dropspace::model_profile profile) {
        const auto policy = dropspace::policy_for(profile);
        fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK);
        struct stat before{}, after{};
        if (!policy || fd < 0 || fstat(fd, &before) != 0 || !S_ISREG(before.st_mode) ||
            before.st_nlink != 1 || uint64_t(before.st_size) != policy->bytes) {
            if (fd >= 0) close(fd); fd = -1; throw std::runtime_error("model-file");
        }
        CC_SHA256_CTX state; CC_SHA256_Init(&state);
        std::vector<unsigned char> bytes(1024 * 1024);
        uint64_t total = 0;
        while (true) {
            const auto count = read(fd, bytes.data(), bytes.size());
            if (count < 0 && errno == EINTR) continue;
            if (count < 0) { close(fd); fd = -1; throw std::runtime_error("model-read"); }
            if (!count) break;
            total += uint64_t(count);
            if (total > policy->bytes) { close(fd); fd = -1; throw std::runtime_error("model-size"); }
            CC_SHA256_Update(&state, bytes.data(), CC_LONG(count));
        }
        unsigned char digest[CC_SHA256_DIGEST_LENGTH]; CC_SHA256_Final(digest, &state);
        constexpr char alphabet[] = "0123456789abcdef"; std::string hash;
        for (auto value : digest) { hash += alphabet[value >> 4]; hash += alphabet[value & 15]; }
        const char * expected = profile == dropspace::model_profile::hy_mt2_7b_q8
            ? "58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0"
            : "5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4";
        if (total != policy->bytes || hash != expected || fstat(fd, &after) != 0 ||
            before.st_size != after.st_size || before.st_mtimespec.tv_sec != after.st_mtimespec.tv_sec ||
            before.st_mtimespec.tv_nsec != after.st_mtimespec.tv_nsec || lseek(fd, 0, SEEK_SET) < 0) {
            close(fd); fd = -1; throw std::runtime_error("model-checksum");
        }
    }
    ~verified_translation_model() { if (fd >= 0) close(fd); }
    verified_translation_model(const verified_translation_model &) = delete;
    std::string path() const { return "/dev/fd/" + std::to_string(fd); }
};
static double elapsed_ms(clock_type::time_point start) {
    return std::chrono::duration<double, std::milli>(clock_type::now() - start).count();
}

// Only the frozen official instructions qualify. Arbitrary/selector prompts never
// contribute to this cache, even if their token IDs share an apparent prefix.
static std::string fixed_instruction(const std::string & input) {
    for (const auto * instruction : {
        u8"将以下文本翻译为简体中文，注意只需要输出翻译后的结果，不要额外解释：\n",
        u8"将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\n"}) {
        const std::string prefix = instruction;
        if (input.size() > prefix.size() && input.compare(0, prefix.size(), prefix) == 0) return prefix;
    }
    return {};
}

static std::string safe_prefix_bytes(const std::string & full, const std::string & fixed,
                                    const std::string & instruction) {
    if (instruction.empty()) return {};
    const auto a = full.find(instruction), b = fixed.find(instruction);
    if (a == std::string::npos || b == std::string::npos ||
        full.find(instruction, a + instruction.size()) != std::string::npos ||
        fixed.find(instruction, b + instruction.size()) != std::string::npos) return {};
    const auto before_source = full.substr(0, a + instruction.size());
    // Templates reflecting source anywhere before the instruction fail closed.
    return before_source == fixed.substr(0, b + instruction.size()) ? before_source : std::string{};
}

static size_t reusable_prefix_length(const std::vector<llama_token> & full,
                                     const std::vector<llama_token> & before_source) {
    if (full.empty() || before_source.empty()) return 0;
    // Drop the boundary token (including any appended special token). Source may
    // merge with it during tokenization. Always evaluate a suffix to obtain logits.
    const auto limit = std::min(full.size() - 1, before_source.size() - 1);
    size_t n = 0;
    while (n < limit && full[n] == before_source[n]) ++n;
    return n;
}

struct prefix_snapshot {
    std::string instruction;
    std::vector<llama_token> tokens;
    std::vector<uint8_t> data;
    static constexpr size_t max_bytes = 16 * 1024 * 1024;

    void discard() { instruction.clear(); tokens.clear(); data.clear(); }
    bool positions_match(llama_context * ctx, size_t count) const {
        auto memory = llama_get_memory(ctx);
        return count > 0 && llama_memory_seq_pos_min(memory, 0) == 0 &&
            llama_memory_seq_pos_max(memory, 0) == static_cast<llama_pos>(count - 1);
    }
    size_t restore(llama_context * ctx, const std::string & fixed,
                   const std::vector<llama_token> & full, size_t safe_count) {
        if (fixed != instruction || safe_count == 0 || data.empty()) { discard(); return 0; }
        size_t count = 0;
        while (count < std::min(tokens.size(), safe_count) && tokens[count] == full[count]) ++count;
        if (!count) { discard(); return 0; }
        if (llama_state_seq_set_data(ctx, data.data(), data.size(), 0) != data.size() ||
            !positions_match(ctx, tokens.size()) ||
            !llama_memory_seq_rm(llama_get_memory(ctx), 0, static_cast<llama_pos>(count), -1) ||
            !positions_match(ctx, count)) {
            // Restoration/removal failures must not leave partial or stale state.
            llama_memory_clear(llama_get_memory(ctx), true); discard(); return 0;
        }
        return count;
    }
    void save(llama_context * ctx, const std::string & fixed,
              const std::vector<llama_token> & full, size_t count) {
        discard();
        if (!count || !llama_memory_seq_rm(llama_get_memory(ctx), 0, static_cast<llama_pos>(count), -1) ||
            !positions_match(ctx, count)) return;
        // Sequence state contains only live KV cells/positions, not logits, sampler
        // history or deleted source/generation cells. Never serialize whole context.
        const auto bytes = llama_state_seq_get_size(ctx, 0);
        if (!bytes || bytes > max_bytes) return;
        std::vector<uint8_t> saved(bytes);
        if (llama_state_seq_get_data(ctx, saved.data(), saved.size(), 0) != bytes) return;
        instruction = fixed; tokens.assign(full.begin(), full.begin() + count); data = std::move(saved);
    }
};

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


// Public Mach VM counters provide an estimate, not a Windows commit-limit equivalent.
static uint64_t reclaimable_host_bytes() {
    vm_statistics64_data_t vm{};
    mach_msg_type_number_t count = HOST_VM_INFO64_COUNT;
    const auto host = mach_host_self();
    const auto status = host_statistics64(host, HOST_VM_INFO64, reinterpret_cast<host_info64_t>(&vm), &count);
    mach_port_deallocate(mach_task_self(), host);
    const auto page = sysconf(_SC_PAGESIZE);
    if (status != KERN_SUCCESS || page <= 0) return 0;
    return (uint64_t(vm.free_count) + uint64_t(vm.inactive_count)) * uint64_t(page);
}

int main(int argc, char ** argv) {
    try {
        if (argc == 2 && std::string(argv[1]) == "--capabilities") {
            common_init(); llama_backend_init();
            id<MTLDevice> device = MTLCreateSystemDefaultDevice();
            send({{"protocol", 1}, {"platform", "macos"}, {"architecture", "arm64"},
                {"metalAvailable", device != nil && ggml_backend_reg_by_name("MTL") != nullptr},
                {"unifiedMemory", device ? bool(device.hasUnifiedMemory) : false},
                {"recommendedWorkingSetBytes", device ? uint64_t(device.recommendedMaxWorkingSetSize) : 0},
                {"reclaimableHostBytesEstimate", reclaimable_host_bytes()}});
            llama_backend_free(); return 0;
        }
        if (argc == 2 && std::string(argv[1]) == "--version") {
            send({{"protocol", 1}, {"profile", "hy-q8-plain-resident-v1"},
                {"engineCommit", "7fe450e19305b828c199d602c23a8337aaa1f03b"},
                {"platform", "macos"}, {"architecture", "arm64"}}); return 0;
        }
        if (argc < 5 || std::string(argv[1]) != "--model" || std::string(argv[3]) != "--mode") return 64;
        auto model_profile = dropspace::model_profile::hy_mt2_1_8b_q8;
        bool timings_enabled = false, prefix_enabled = false, profile_selected = false;
        for (int i = 5; i < argc; ++i) {
            const std::string option = argv[i];
            if (option == "--timings" && !timings_enabled) { timings_enabled = true; continue; }
            if (option == "--experimental-prefix-kv" && !prefix_enabled) { prefix_enabled = true; continue; }
            if (option != "--model-profile" || profile_selected || ++i == argc) return 64;
            const auto requested = dropspace::parse_model_profile(argv[i]);
            if (!requested) return 64;
            model_profile = *requested; profile_selected = true;
        }
        const std::string mode = argv[4];
        if (mode != "cpu" && mode != "metal") return 64;

        // Verification may activate the exact file-backed pages that inference
        // will map. Take admission before reading them, rather than charging the
        // same model again after its hash pass. The host monitors live pressure
        // and resident bytes throughout startup and generation.
        const uint64_t admitted_available = reclaimable_host_bytes();
        std::unique_ptr<verified_translation_model> verified;
        try { verified = std::make_unique<verified_translation_model>(argv[2], model_profile); }
        catch (const std::exception &) { return 67; }
        // Use pinned upstream argument parsing to freeze defaults, explicit sampler flags and
        // model-metadata override bits exactly like the evaluated llama-completion command.
        std::vector<std::string> args = {"plain-lyrics-worker", "-m", verified->path(), "--offline", "--perf", "--no-escape",
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
        json selected_device;
        if (mode == "metal") {
            id<MTLDevice> metal = MTLCreateSystemDefaultDevice();
            if (!metal) return 65;
            const auto policy = dropspace::policy_for(model_profile);
            const uint64_t available = admitted_available;
            const uint64_t recommended = metal.recommendedMaxWorkingSetSize;
            const uint64_t reserve = std::max(dropspace::gib, recommended / 5);
            if (!policy || available < policy->bytes + policy->host_allowance ||
                recommended < policy->bytes + policy->gpu_allowance + reserve) return 66;
            auto reg = ggml_backend_reg_by_name("MTL");
            if (!reg || ggml_backend_reg_dev_count(reg) == 0) return 65;
            auto device = ggml_backend_reg_dev_get(reg, 0);
            params.devices = {device, nullptr}; params.n_gpu_layers = 999;
            selected_device = {{"name", std::string(metal.name.UTF8String)}, {"unifiedMemory", bool(metal.hasUnifiedMemory)},
                {"reclaimableHostBytesEstimate", available}, {"recommendedWorkingSetBytes", recommended}};
        }
        auto loaded = common_init_from_params(params);
        auto model = loaded->model(); auto ctx = loaded->context();
        if (!model || !ctx) return 67;
        auto templates = common_chat_templates_init(model, params.chat_template);
        const bool chat = common_chat_templates_was_explicit(templates.get());
        auto vocab = llama_model_get_vocab(model);
        json ready = {{"protocol", 1}, {"selectionProtocol", 2}, {"ready", true}, {"backend", mode},
            {"engineCommit", "7fe450e19305b828c199d602c23a8337aaa1f03b"},
            {"modelProfile", std::string(dropspace::policy_for(model_profile)->id)}};
        if (prefix_enabled) ready["experimentalPrefixKv"] = true;
        if (mode == "metal") ready["device"] = selected_device;
        send(ready);
        prefix_snapshot cached_prefix; // Process/context/model owned; never persisted to disk.
        const auto render = [&](const std::string & content) {
            if (!chat) return content;
            common_chat_templates_inputs inputs;
            inputs.use_jinja = params.use_jinja;
            common_chat_msg message; message.role = "user"; message.content = content;
            inputs.messages = {message}; inputs.add_generation_prompt = true;
            inputs.force_pure_content = params.force_pure_content_parser;
            // Matches pinned completion.cpp's initial single-message template application.
            return common_chat_templates_apply(templates.get(), inputs).prompt;
        };
        std::string frame;
        while (read_frame(frame)) {
            const auto request_start = clock_type::now();
            const auto request = json::parse(frame);
            if (!request.is_object() || request.size() != 3 ||
                (request.at("protocol") != 1 && request.at("protocol") != 2) ||
                !request.at("id").is_string() || !request.at("prompt").is_string()) return 68;
            const bool selection = request.at("protocol") == 2;
            const auto id = request.at("id").get<std::string>();
            const auto input = request.at("prompt").get<std::string>();
            if (id.size() != 32 || id.find_first_not_of("0123456789abcdef") != std::string::npos ||
                input.empty() || input.size() > (selection ? 8192u : 1800u)) return 68;
            // Keep complete zero-clear even in the experiment. Restore only a verified
            // fixed-prefix sequence snapshot; sampler and single-message history stay new.
            auto stage_start = clock_type::now();
            llama_memory_clear(llama_get_memory(ctx), true);
            const double reset_ms = elapsed_ms(stage_start);
            struct clear_on_exit {
                llama_context * ctx; prefix_snapshot & cached; bool active = true;
                void finish() { llama_memory_clear(llama_get_memory(ctx), true); active = false; }
                ~clear_on_exit(){ if (active) { cached.discard(); llama_memory_clear(llama_get_memory(ctx), true); } }
            } clear{ctx, cached_prefix};
            stage_start = clock_type::now();
            auto sampling = params.sampling;
            std::unique_ptr<common_sampler, decltype(&common_sampler_free)> sampler(common_sampler_init(model, sampling), common_sampler_free);
            if (!sampler) return 69;
            const double sampler_ms = elapsed_ms(stage_start);
            stage_start = clock_type::now();
            const auto prompt = render(input);
            const double template_ms = elapsed_ms(stage_start);
            stage_start = clock_type::now();
            auto tokens = common_tokenize(ctx, prompt, true, true);
            const double tokenize_ms = elapsed_ms(stage_start);
            if (tokens.empty() || tokens.size() + (selection ? 32u : 2048u) >= 4096) return 70;
            stage_start = clock_type::now();
            const auto instruction = prefix_enabled && !selection ? fixed_instruction(input) : std::string{};
            size_t safe_count = 0;
            if (!instruction.empty()) {
                const auto before_source = safe_prefix_bytes(prompt, render(instruction), instruction);
                if (!before_source.empty())
                    safe_count = reusable_prefix_length(tokens, common_tokenize(ctx, before_source, true, true));
            }
            const double prefix_plan_ms = elapsed_ms(stage_start);
            stage_start = clock_type::now();
            const auto reused = prefix_enabled ? cached_prefix.restore(ctx, instruction, tokens, safe_count) : 0;
            if (timings_enabled && reused) llama_synchronize(ctx);
            const double prefix_restore_ms = elapsed_ms(stage_start);
            stage_start = clock_type::now();
            for (auto token : tokens) common_sampler_accept(sampler.get(), token, false);
            const double sampler_accept_ms = elapsed_ms(stage_start);
            stage_start = clock_type::now();
            for (size_t offset = reused; offset < tokens.size(); offset += params.n_batch) {
                const auto n = std::min<size_t>(params.n_batch, tokens.size() - offset);
                if (llama_decode(ctx, llama_batch_get_one(tokens.data() + offset, static_cast<int32_t>(n)))) return 71;
            }
            // Attribute asynchronous backend work to its stage, rather than the next sample.
            // The default path keeps its existing synchronization behavior.
            if (timings_enabled) llama_synchronize(ctx);
            const double prefill_ms = elapsed_ms(stage_start);
            double decode_ms = 0, sample_ms = 0;
            size_t output_tokens = 0, sampled_tokens = 0;
            const auto generation_start = clock_type::now();
            std::string output; bool complete = false;
            for (int i = 0; i < (selection ? 32 : 2048); ++i) {
                stage_start = clock_type::now();
                auto token = common_sampler_sample(sampler.get(), ctx, -1);
                common_sampler_accept(sampler.get(), token, true);
                sample_ms += elapsed_ms(stage_start); ++sampled_tokens;
                if (llama_vocab_is_eog(vocab, token)) { complete = true; break; }
                ++output_tokens;
                output += common_token_to_piece(ctx, token, true);
                if (output.size() > (selection ? 128u : 16384u)) return 72;
                stage_start = clock_type::now();
                if (llama_decode(ctx, llama_batch_get_one(&token, 1))) return 71;
                if (timings_enabled) llama_synchronize(ctx);
                decode_ms += elapsed_ms(stage_start);
            }
            const double generation_ms = elapsed_ms(generation_start);
            stage_start = clock_type::now();
            if (prefix_enabled && complete) cached_prefix.save(ctx, instruction, tokens, safe_count);
            else cached_prefix.discard();
            const double prefix_save_ms = elapsed_ms(stage_start);
            stage_start = clock_type::now();
            // Default response/cleanup ordering remains the legacy ordering. Timed
            // and prefix experiments complete cleanup before reporting their result.
            if (timings_enabled || prefix_enabled) clear.finish();
            const double cleanup_ms = elapsed_ms(stage_start);
            // Never present a token-cap truncation as a complete translation.
            json response = {{"protocol", selection ? 2 : 1}, {"id", id}, {"complete", complete}, {"text", output}};
            if (timings_enabled) response["timings"] = {
                {"schemaVersion", 1}, {"totalMs", elapsed_ms(request_start)}, {"resetMs", reset_ms},
                {"samplerInitMs", sampler_ms}, {"templateMs", template_ms}, {"tokenizeMs", tokenize_ms},
                {"samplerAcceptMs", sampler_accept_ms}, {"prefillMs", prefill_ms}, {"decodeMs", decode_ms},
                {"sampleMs", sample_ms}, {"generationMs", generation_ms}, {"cleanupMs", cleanup_ms},
                {"inputTokens", tokens.size()}, {"prefillTokens", tokens.size() - reused}, {"outputTokens", output_tokens},
                {"sampledTokens", sampled_tokens}, {"prefixReusedTokens", reused},
                {"prefixPlanMs", prefix_plan_ms}, {"prefixRestoreMs", prefix_restore_ms}, {"prefixSaveMs", prefix_save_ms},
                {"prefixRetainedTokens", cached_prefix.tokens.size()}, {"prefixSnapshotBytes", cached_prefix.data.size()}};
            send(response);
        }
        return 0;
    } catch (const std::exception &) { return 73; } // never echo private prompt/output to diagnostics
}
