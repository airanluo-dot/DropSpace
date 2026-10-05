// Real pinned-model snapshot failure checks; reuse the existing CPU engine build.
#define main dropspace_worker_entry
#include "main.cpp"
#undef main
#include <cassert>

int main(int argc, char ** argv) {
    assert(argc == 2);
    assert(std::filesystem::file_size(argv[1]) == 1908528192u);
    llama_log_set([](ggml_log_level, const char *, void *){}, nullptr);
    llama_backend_init();
    auto mp = llama_model_default_params(); mp.n_gpu_layers = 0;
    std::unique_ptr<llama_model, decltype(&llama_model_free)> model(llama_model_load_from_file(argv[1], mp), llama_model_free);
    assert(model);
    auto cp = llama_context_default_params();
    cp.n_ctx = 128; cp.n_batch = 128; cp.n_ubatch = 128; cp.n_threads = 4; cp.n_threads_batch = 4;
    std::unique_ptr<llama_context, decltype(&llama_free)> ctx(llama_init_from_model(model.get(), cp), llama_free);
    assert(ctx);
    auto memory = llama_get_memory(ctx.get());
    const std::string instruction = u8"将以下文本翻译为简体中文，注意只需要输出翻译后的结果，不要额外解释：\n";
    auto full = common_tokenize(ctx.get(), instruction + "The moon shines above the quiet river.", true, true);
    const auto count = reusable_prefix_length(full, common_tokenize(ctx.get(), instruction, true, true));
    assert(count > 1 && count < full.size());
    prefix_snapshot snapshot;
    const auto save = [&]() {
        llama_memory_clear(memory, true);
        assert(llama_decode(ctx.get(), llama_batch_get_one(full.data(), static_cast<int32_t>(full.size()))) == 0);
        snapshot.save(ctx.get(), instruction, full, count);
        assert(snapshot.tokens.size() == count && !snapshot.data.empty());
        assert(snapshot.positions_match(ctx.get(), count));
        llama_memory_clear(memory, true);
        assert(llama_memory_seq_pos_max(memory, 0) == -1);
    };
    save();
    assert(snapshot.restore(ctx.get(), instruction, full, count) == count);
    assert(snapshot.positions_match(ctx.get(), count));

    // A smaller actual safe prefix must remove all restored positions after it.
    save();
    assert(snapshot.restore(ctx.get(), instruction, full, count - 1) == count - 1);
    assert(snapshot.positions_match(ctx.get(), count - 1));

    // Corrupted native state and inconsistent position metadata must fail cleanly.
    save(); snapshot.data[0] ^= 0xff;
    assert(snapshot.restore(ctx.get(), instruction, full, count) == 0);
    assert(snapshot.data.empty() && snapshot.tokens.empty());
    assert(llama_memory_seq_pos_max(memory, 0) == -1);
    save(); snapshot.tokens.push_back(full[count]);
    assert(snapshot.restore(ctx.get(), instruction, full, count) == 0);
    assert(snapshot.data.empty() && llama_memory_seq_pos_max(memory, 0) == -1);

    save();
    assert(snapshot.restore(ctx.get(), "other target", full, count) == 0);
    assert(snapshot.data.empty() && llama_memory_seq_pos_max(memory, 0) == -1);
    save();
    assert(snapshot.restore(ctx.get(), instruction, full, 0) == 0);
    assert(snapshot.data.empty() && llama_memory_seq_pos_max(memory, 0) == -1);
    save();
    auto different = full; different[0] = different[0] == 0 ? 1 : 0;
    assert(snapshot.restore(ctx.get(), instruction, different, count) == 0);
    assert(snapshot.data.empty() && llama_memory_seq_pos_max(memory, 0) == -1);

    // Snapshot save cannot bless an absent or incomplete prefix range.
    snapshot.save(ctx.get(), instruction, full, count);
    assert(snapshot.data.empty());
}
