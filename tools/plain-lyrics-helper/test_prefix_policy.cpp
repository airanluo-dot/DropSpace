// Link against the same pinned llama-common build; this test loads no model.
#define main dropspace_worker_entry
#include "main.cpp"
#undef main
#include <cassert>

int main() {
    const std::string zh = u8"将以下文本翻译为简体中文，注意只需要输出翻译后的结果，不要额外解释：\n";
    const std::string en = u8"将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\n";
    assert(fixed_instruction(zh + "hello") == zh);
    assert(fixed_instruction(en + u8"你好") == en);
    assert(fixed_instruction(zh).empty());
    assert(fixed_instruction("Choose one number: 0.").empty());
    assert(fixed_instruction("x" + zh + "hello").empty());

    const std::string head = "<user>";
    const std::string tail = "</user><assistant>";
    assert(safe_prefix_bytes(head + zh + "hello" + tail, head + zh + tail, zh) == head + zh);
    assert(safe_prefix_bytes(zh + "hello", zh, zh) == zh);
    // Templates reflecting source before the instruction, changed wrappers, missing or
    // duplicate instruction text cannot promote any source text to the fixed snapshot.
    assert(safe_prefix_bytes("hello" + head + zh + "hello" + tail, head + zh + tail, zh).empty());
    assert(safe_prefix_bytes(head + zh + "hello" + tail, "other" + zh + tail, zh).empty());
    assert(safe_prefix_bytes(head + en + "hello" + tail, head + zh + tail, zh).empty());
    assert(safe_prefix_bytes(head + zh + zh + "hello" + tail, head + zh + tail, zh).empty());
    assert(safe_prefix_bytes(head + zh + "hello" + tail, head + zh + zh + tail, zh).empty());

    // Drop the boundary token before comparing actual post-template token IDs.
    // This leaves merged source/boundary tokens and at least the final prompt token
    // for fresh evaluation, which also ensures valid generation logits.
    assert(reusable_prefix_length({1, 2, 3, 4, 5}, {1, 2, 3, 4}) == 3);
    assert(reusable_prefix_length({1, 2, 9, 4, 5}, {1, 2, 3, 4}) == 2);
    assert(reusable_prefix_length({1, 2, 3}, {1, 2, 3, 4}) == 2);
    assert(reusable_prefix_length({9, 2, 3}, {1, 2, 3}) == 0);
    assert(reusable_prefix_length({1, 2, 3}, {1}) == 0);
    assert(reusable_prefix_length({1, 2, 3}, {}) == 0);
    assert(reusable_prefix_length({}, {1, 2, 3}) == 0);
}
