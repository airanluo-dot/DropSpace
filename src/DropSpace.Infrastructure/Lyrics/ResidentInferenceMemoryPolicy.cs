using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Host commit/working-set limits; these never relax native measured-VRAM admission.</summary>
internal static class ResidentInferenceMemoryPolicy
{
    // The installed CUDA13 worker hits JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT under the
    // CPU profile's 3 GiB cap while creating the 1.8B model/context. CUDA DLL/driver and
    // context commit require a separate bounded allowance, even with ample free VRAM.
    internal const long Hy18CudaMaximumBytes = 5L * 1024 * 1024 * 1024;

    internal static long MaximumBytes(string modelHash, string backend) =>
        backend == "cuda" && string.Equals(modelHash, AiLyricsModelCatalog.ExperimentalPlain.Sha256, StringComparison.OrdinalIgnoreCase)
            ? Hy18CudaMaximumBytes : LlamaCompletionRunner.MemoryBudgetFor(modelHash);
}
