from pathlib import Path
import unittest
from adapt_worker import adapt

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / 'tools/plain-lyrics-helper/main.cpp').read_text()

class Contract(unittest.TestCase):
    def test_request_loop_and_frozen_sampler_are_byte_identical(self):
        generated = adapt(SOURCE)
        self.assertEqual(SOURCE[SOURCE.index('        std::string frame;'):],
                         generated[generated.index('        std::string frame;'):])
        start, end = '        std::vector<std::string> args =', '        std::vector<char *> argv_fixed;'
        self.assertEqual(SOURCE[SOURCE.index(start):SOURCE.index(end)],
                         generated[generated.index(start):generated.index(end)])
        self.assertNotIn('DROPSPACE_VULKAN', generated)
        self.assertNotIn('"vulkan"', generated)
        self.assertIn('if (mode != "cuda") return 64;', generated)
        self.assertIn('ready["componentId"]', generated)

    def test_changed_backend_anchors_fail_closed(self):
        with self.assertRaises(ValueError):
            adapt(SOURCE.replace('if (mode != "cpu" && mode != "vulkan") return 64;', '// backend changed'))
        with self.assertRaises(ValueError):
            adapt(SOURCE + '\n#ifdef DROPSPACE_VULKAN\n#endif\n')

    def test_cuda_policy_conservative_boundaries(self):
        # Compile the shared actual GPU policy; these are thresholds, not measured peaks.
        import subprocess, tempfile
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / 'policy.cpp'
            source.write_text('''#include "gpu-policy.h"
#include <cassert>
int main() {
 using namespace dropspace;
 for (auto profile : {model_profile::hy_mt2_1_8b_q8, model_profile::hy_mt2_7b_q8}) {
   auto p = policy_for(profile); auto total = 16*gib; auto needed = p->bytes + p->gpu_allowance + total/5;
   assert(gpu_fits(0x10de, true, true, needed, total, 0, profile));
   assert(!gpu_fits(0x10de, true, true, needed-1, total, 0, profile));
   assert(!gpu_fits(0x10de, true, false, needed, total, 0, profile));
   assert(!gpu_fits(0x10de, true, true, total+1, total, 0, profile));
   assert(!gpu_fits(0x10de, false, true, needed, total, 0, profile));
 }
 assert(!gpu_fits(0x8086, true, true, 16*gib, 16*gib));
}''')
            binary = Path(tmp) / 'policy'
            subprocess.run(['g++', '-std=c++17', '-I' + str(ROOT / 'tools/plain-lyrics-helper'), str(source), '-o', str(binary)], check=True)
            subprocess.run([str(binary)], check=True)

if __name__ == '__main__':
    unittest.main()
