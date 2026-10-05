"""Backend-only build overlay; never edits or duplicates the maintained request loop.

Structural anchors fail closed when the maintained worker changes its backend scaffolding.
The unchanged request loop automatically follows the separate KV task's main.cpp updates.
"""
from pathlib import Path
import argparse

COMPONENT = 'llama-cpp-v0.5.0-cuda13-win-x64-v1'

def replace_once(text, old, new):
    if text.count(old) != 1:
        raise ValueError('CUDA overlay anchor changed: ' + old[:80])
    return text.replace(old, new, 1)

def adapt(text):
    text = replace_once(text, '''#ifdef DROPSPACE_VULKAN
#include <vulkan/vulkan.h>
#ifdef _WIN32
#include <windows.h>
#endif
#endif''', '')
    begin = text.index('#ifdef DROPSPACE_VULKAN\nstatic ggml_backend_dev_t choose_gpu(')
    end = text.index('\n#endif\n\nint main(', begin)
    text = text[:begin] + '#include "cuda-device.h"' + text[end + len('\n#endif'):]
    text = replace_once(text, 'if (mode != "cpu" && mode != "vulkan") return 64;\n#ifndef DROPSPACE_VULKAN\n        if (mode != "cpu") return 65;\n#endif',
                        'if (mode != "cuda") return 64;')
    text = replace_once(text, '''#if defined(DROPSPACE_VULKAN) && defined(_WIN32)
        if (!LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32)) return 65;
#endif''', '')
    text = replace_once(text, '#ifdef DROPSPACE_VULKAN\n        json selected_device;', '        json selected_device;')
    text = replace_once(text, 'if (mode == "vulkan") {', 'if (mode == "cuda") {')
    text = replace_once(text, '\n#endif\n        auto loaded', '\n        auto loaded')
    text = replace_once(text, '#ifdef DROPSPACE_VULKAN\n        // Diagnostic identity', '        // Diagnostic identity')
    text = replace_once(text, 'if (mode == "vulkan") ready["device"] = selected_device;\n#endif',
                        'ready["device"] = selected_device;\n        ready["componentId"] = "' + COMPONENT + '";')
    text = replace_once(text, 'send({{"protocol", 1}, {"profile", "hy-q8-plain-resident-v1"}});',
                        'send({{"protocol", 1}, {"profile", "hy-q8-plain-resident-v1"}, {"backend", "cuda"}, {"componentId", "' + COMPONENT + '"}});')
    if 'DROPSPACE_VULKAN' in text or '"vulkan"' in text:
        raise ValueError('Unexpected residual Vulkan scaffolding')
    return text

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    generated = adapt(args.source.read_text(encoding='utf-8'))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(generated, encoding='utf-8', newline='\n')
