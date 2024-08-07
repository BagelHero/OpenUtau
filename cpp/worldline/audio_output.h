#ifndef WORLDLINE_AUDIO_OUTPUT_H
#define WORLDLINE_AUDIO_OUTPUT_H

#include <stdint.h>

#include "miniaudio.h"

#if defined(_MSC_VER)
#define DLL_API __declspec(dllexport)
#elif defined(__GNUC__)
#define DLL_API __attribute__((visibility("default")))
#endif

#if defined(__cplusplus)
extern "C" {
#endif

typedef struct {
  char* name;
  uint64_t id;
  char* api;
  uint32_t api_id;
} ou_audio_device_info_t;

typedef struct {
  ma_context context;
  ma_device device;
} ou_audio_context_t;

typedef void (*ou_audio_data_callback_t)(float* buffer, uint32_t channels,
                                         uint32_t frame_count);

DLL_API int32_t ou_get_audio_device_infos(ou_audio_device_info_t* device_infos,
                                          int32_t max_count);

DLL_API void ou_free_audio_device_infos(ou_audio_device_info_t* device_infos,
                                        int32_t count);

DLL_API ou_audio_context_t* ou_init_audio_device(
    uint32_t api_id, uint64_t id, ou_audio_data_callback_t callback);

DLL_API int ou_free_audio_device(ou_audio_context_t* context);

DLL_API int ou_audio_device_start(ou_audio_context_t* context);

DLL_API int ou_audio_device_stop(ou_audio_context_t* context);

DLL_API const char* ou_audio_get_error_message(int error_code);

#if defined(__cplusplus)
}
#endif

#endif  // WORLDLINE_AUDIO_OUTPUT_H
