#include "CSpectrumBuffer.h"
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define DS_CAPACITY 16384
_Static_assert(ATOMIC_LLONG_LOCK_FREE == 2, "Audio ring requires lock-free counters");
struct DSSpectrumBuffer {
    _Atomic uint64_t write;
    _Atomic uint64_t read;
    _Atomic uint64_t dropped;
    _Atomic uint64_t packetTime;
    _Atomic int invalid;
    float samples[DS_CAPACITY];
};
DSSpectrumBuffer *DSSpectrumBufferCreate(void) {
    DSSpectrumBuffer *b = calloc(1, sizeof(DSSpectrumBuffer));
    if (b) {
        atomic_init(&b->write, 0); atomic_init(&b->read, 0);
        atomic_init(&b->dropped, 0); atomic_init(&b->packetTime, 0);
        atomic_init(&b->invalid, 0);
    }
    return b;
}
void DSSpectrumBufferDestroy(DSSpectrumBuffer *buffer) { free(buffer); }
void DSSpectrumBufferWrite(DSSpectrumBuffer *b, const float *samples, size_t count) {
    if (count == 0 || atomic_load_explicit(&b->invalid, memory_order_acquire)) return;
    uint64_t write = atomic_load_explicit(&b->write, memory_order_relaxed);
    uint64_t read = atomic_load_explicit(&b->read, memory_order_acquire);
    // Drop the complete packet rather than splice across a discontinuity.
    if (count > DS_CAPACITY - (write - read)) {
        atomic_fetch_add_explicit(&b->dropped, count, memory_order_relaxed);
        return;
    }
    size_t first = DS_CAPACITY - write % DS_CAPACITY;
    if (first > count) first = count;
    memcpy(b->samples + write % DS_CAPACITY, samples, first * sizeof(float));
    memcpy(b->samples, samples + first, (count - first) * sizeof(float));
    atomic_store_explicit(&b->packetTime, clock_gettime_nsec_np(CLOCK_UPTIME_RAW), memory_order_release);
    atomic_store_explicit(&b->write, write + count, memory_order_release);
}
void DSSpectrumBufferWriteAudio(DSSpectrumBuffer *b, const AudioBufferList *audio) {
    // The owner validates a packed native Float32 mono stream before starting IO.
    // Unexpected layouts are ignored; never read a guessed format or another stream.
    if (!audio || audio->mNumberBuffers != 1) return;
    const AudioBuffer *a = &audio->mBuffers[0];
    if (!a->mData || a->mNumberChannels != 1 || a->mDataByteSize % sizeof(float)) return;
    DSSpectrumBufferWrite(b, a->mData, a->mDataByteSize / sizeof(float));
}
size_t DSSpectrumBufferRead(DSSpectrumBuffer *b, float *output, size_t capacity) {
    uint64_t read = atomic_load_explicit(&b->read, memory_order_relaxed);
    uint64_t write = atomic_load_explicit(&b->write, memory_order_acquire);
    size_t count = write - read;
    if (count > capacity) count = capacity;
    size_t first = DS_CAPACITY - read % DS_CAPACITY;
    if (first > count) first = count;
    memcpy(output, b->samples + read % DS_CAPACITY, first * sizeof(float));
    memcpy(output + first, b->samples, (count - first) * sizeof(float));
    atomic_store_explicit(&b->read, read + count, memory_order_release);
    return count;
}
uint64_t DSSpectrumBufferDropped(DSSpectrumBuffer *b) {
    return atomic_load_explicit(&b->dropped, memory_order_relaxed);
}
double DSSpectrumBufferLastPacketTime(DSSpectrumBuffer *b) {
    return (double)atomic_load_explicit(&b->packetTime, memory_order_acquire) / 1e9;
}
double DSSpectrumMonotonicTime(void) { return (double)clock_gettime_nsec_np(CLOCK_UPTIME_RAW) / 1e9; }
void DSSpectrumBufferInvalidate(DSSpectrumBuffer *b) { atomic_store_explicit(&b->invalid, 1, memory_order_release); }
int DSSpectrumBufferIsValid(DSSpectrumBuffer *b) { return !atomic_load_explicit(&b->invalid, memory_order_acquire); }
