#ifndef DS_SPECTRUM_BUFFER_H
#define DS_SPECTRUM_BUFFER_H
#include <stdint.h>
#include <stddef.h>
#include <CoreAudio/CoreAudio.h>

typedef struct DSSpectrumBuffer DSSpectrumBuffer;
// One HAL producer, one actor consumer. Never allocates or waits in the callback.
DSSpectrumBuffer *DSSpectrumBufferCreate(void);
void DSSpectrumBufferDestroy(DSSpectrumBuffer *buffer);
void DSSpectrumBufferWrite(DSSpectrumBuffer *buffer, const float *samples, size_t count);
void DSSpectrumBufferWriteAudio(DSSpectrumBuffer *buffer, const AudioBufferList *audio);
size_t DSSpectrumBufferRead(DSSpectrumBuffer *buffer, float *output, size_t capacity);
uint64_t DSSpectrumBufferDropped(DSSpectrumBuffer *buffer);
double DSSpectrumBufferLastPacketTime(DSSpectrumBuffer *buffer);
double DSSpectrumMonotonicTime(void);
void DSSpectrumBufferInvalidate(DSSpectrumBuffer *buffer);
int DSSpectrumBufferIsValid(DSSpectrumBuffer *buffer);
#endif
