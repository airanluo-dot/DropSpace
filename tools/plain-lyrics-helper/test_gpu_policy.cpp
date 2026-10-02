#include "gpu-policy.h"
#include <cassert>
int main() {
    using namespace dropspace;
    assert(gpu_fits(0x10de, true, true, 6*gib, 8*gib));
    assert(gpu_fits(0x1002, true, true, 6*gib, 8*gib));
    assert(!gpu_fits(0x1002, true, false, 6*gib, 8*gib));
    assert(!gpu_fits(0x10de, true, true, 2*gib, 8*gib));
    assert(!gpu_fits(0x10de, true, true, 9*gib, 8*gib));
    assert(!gpu_fits(0x8086, true, true, 6*gib, 8*gib));
    assert(!gpu_fits(0x1002, false, true, 6*gib, 8*gib));
    assert(!gpu_fits(0x1002, false, true, 6*gib, 8*gib, 3*gib));
    assert(gpu_fits(0x1002, false, true, 6*gib, 8*gib, 8*gib));
}
