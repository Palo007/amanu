// Runs the C++ WebRTC APM (the M145 tree Sonora was ported from) on raw
// 16 kHz mono float files the way Amanu drives Sonora: echo canceller only,
// 10 ms frames, render frame before capture frame. Writes the cleaned mic and,
// per frame, AEC3's delay and ERLE, so the C++ and Rust runs can be compared
// on identical input.
//
//   aec3-file <far.f32> <mic.f32> <out.f32> <stats.txt> [blocks] [delay_ms]
//
// blocks: linear filter length in 4 ms blocks (all four filters), 0 = default.
// delay_ms: external delay estimate in ms; omitted or negative = AEC3's own.
#include <cstdio>
#include <algorithm>
#include <cstdlib>
#include <optional>
#include <fstream>
#include <vector>

#include "api/audio/builtin_audio_processing_builder.h"
#include "api/audio/echo_canceller3_config.h"
#include "api/environment/environment_factory.h"
#include "api/scoped_refptr.h"
#include <webrtc/modules/audio_processing/include/audio_processing.h>

static std::vector<float> read_all(const char* path) {
  std::ifstream f(path, std::ios::binary | std::ios::ate);
  std::vector<float> v(static_cast<size_t>(f.tellg()) / sizeof(float));
  f.seekg(0);
  f.read(reinterpret_cast<char*>(v.data()), v.size() * sizeof(float));
  return v;
}

int main(int argc, char** argv) {
  if (argc < 5) {
    std::fprintf(stderr, "usage: %s far.f32 mic.f32 out.f32 stats.txt [blocks] [delay_ms]\n", argv[0]);
    return 2;
  }
  const int blocks = argc > 5 ? std::atoi(argv[5]) : 0;
  const int delay_ms = argc > 6 ? std::atoi(argv[6]) : -1;
  std::vector<float> far = read_all(argv[1]);
  std::vector<float> mic = read_all(argv[2]);

  webrtc::AudioProcessing::Config config;
  config.echo_canceller.enabled = true;
  config.echo_canceller.enforce_high_pass_filtering = false;
  config.high_pass_filter.enabled = false;
  config.noise_suppression.enabled = false;
  config.gain_controller1.enabled = false;
  config.gain_controller2.enabled = false;

  webrtc::EchoCanceller3Config aec3;
  if (blocks > 0) {
    aec3.filter.refined.length_blocks = blocks;
    aec3.filter.coarse.length_blocks = blocks;
    aec3.filter.refined_initial.length_blocks = blocks;
    aec3.filter.coarse_initial.length_blocks = blocks;
  }
  if (delay_ms >= 0) aec3.delay.use_external_delay_estimator = true;

  webrtc::Environment env = webrtc::CreateEnvironment();
  webrtc::BuiltinAudioProcessingBuilder builder(config);
  builder.SetEchoCancellerConfig(aec3, std::nullopt);
  webrtc::scoped_refptr<webrtc::AudioProcessing> apm = builder.Build(env);

  webrtc::StreamConfig stream(16000, 1);
  const size_t frame = 160;
  const size_t frames = std::min(far.size(), mic.size()) / frame;
  std::vector<float> out(frames * frame, 0.0f);
  std::FILE* stats = std::fopen(argv[4], "w");
  std::vector<float> far_out(frame), far_in(frame), mic_in(frame);
  for (size_t k = 0; k < frames; ++k) {
    std::copy(far.begin() + k * frame, far.begin() + (k + 1) * frame, far_in.begin());
    std::copy(mic.begin() + k * frame, mic.begin() + (k + 1) * frame, mic_in.begin());
    const float* src[1] = {far_in.data()};
    float* dst[1] = {far_out.data()};
    if (apm->ProcessReverseStream(src, stream, stream, dst) != 0) return 3;
    if (delay_ms >= 0) apm->set_stream_delay_ms(delay_ms);
    const float* msrc[1] = {mic_in.data()};
    float* mdst[1] = {out.data() + k * frame};
    if (apm->ProcessStream(msrc, stream, stream, mdst) != 0) return 4;
    webrtc::AudioProcessingStats s = apm->GetStatistics();
    std::fprintf(stats, "%d %.2f\n", s.delay_ms.value_or(-1), s.echo_return_loss_enhancement.value_or(0.0));
  }
  std::fclose(stats);
  std::ofstream o(argv[3], std::ios::binary);
  o.write(reinterpret_cast<const char*>(out.data()), out.size() * sizeof(float));
  return 0;
}
