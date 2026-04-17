using System;
using System.Collections.Generic;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;

namespace LiveKit
{
    /// <summary>
    /// Thin wrapper over LiveKit FFI APM APIs.
    /// Splits arbitrary PCM buffers into 10 ms frames and feeds them to WebRTC APM.
    /// </summary>
    public sealed class AudioProcessingModule : IDisposable
    {
        private sealed class StreamState
        {
            public readonly Queue<short> InputQueue = new();
            public readonly Queue<short> OutputQueue = new();
            public short[] FrameBuffer = Array.Empty<short>();
            public int SampleRate;
            public int Channels;
            public int FrameSamples;
            public bool Primed;

            public void Reset(int sampleRate, int channels)
            {
                InputQueue.Clear();
                OutputQueue.Clear();
                SampleRate = sampleRate;
                Channels = channels;
                FrameSamples = GetTenMsFrameSampleCount(sampleRate, channels);
                FrameBuffer = FrameBuffer.Length == FrameSamples ? FrameBuffer : new short[FrameSamples];
                Primed = false;
            }
        }

        private readonly FfiHandle _handle;
        private readonly object _lock = new object();
        private readonly StreamState _streamState = new StreamState();
        private readonly StreamState _reverseState = new StreamState();
        private short[] _captureUpsampleScratch = Array.Empty<short>();
        private short[] _reverseUpsampleScratch = Array.Empty<short>();
        private bool _disposed;

        public AudioProcessingModule(bool echoCancellerEnabled, bool gainControllerEnabled, bool highPassFilterEnabled, bool noiseSuppressionEnabled)
        {
            using var request = FFIBridge.Instance.NewRequest<NewApmRequest>();
            var newApm = request.request;
            newApm.EchoCancellerEnabled = echoCancellerEnabled;
            newApm.GainControllerEnabled = gainControllerEnabled;
            newApm.HighPassFilterEnabled = highPassFilterEnabled;
            newApm.NoiseSuppressionEnabled = noiseSuppressionEnabled;

            using var response = request.Send();
            FfiResponse res = response;
            if (res.NewApm?.Apm == null)
            {
                throw new InvalidOperationException("Failed to create APM handle");
            }

            _handle = FfiHandle.FromOwnedHandle(res.NewApm.Apm.Handle);
        }

        public void ProcessStream(short[] pcm, int sampleRate, int channels)
        {
            ProcessInPlace(pcm, sampleRate, channels, _streamState, ProcessStreamFrame, writeBack: true, isReverse: false);
        }

        public void ProcessReverseStream(short[] pcm, int sampleRate, int channels)
        {
            ProcessInPlace(pcm, sampleRate, channels, _reverseState, ProcessReverseStreamFrame, writeBack: false, isReverse: true);
        }

        public void SetStreamDelayMs(int delayMs)
        {
            if (delayMs < 0)
            {
                delayMs = 0;
            }

            lock (_lock)
            {
                ThrowIfDisposed();
                using var request = FFIBridge.Instance.NewRequest<ApmSetStreamDelayRequest>();
                var setDelay = request.request;
                setDelay.ApmHandle = (ulong)_handle.DangerousGetHandle();
                setDelay.DelayMs = delayMs;

                using var response = request.Send();
                FfiResponse res = response;
                if (res.ApmSetStreamDelay != null && !string.IsNullOrEmpty(res.ApmSetStreamDelay.Error))
                {
                    Utils.Warning($"ApmSetStreamDelay failed: {res.ApmSetStreamDelay.Error}");
                }
            }
        }

        private void ProcessInPlace(short[] pcm, int sampleRate, int channels, StreamState state, Action<short[], int, int> processor, bool writeBack, bool isReverse)
        {
            if (pcm == null || pcm.Length == 0)
            {
                return;
            }

            if (channels <= 0 || sampleRate <= 0)
            {
                return;
            }

            if (!IsSupportedChannels(channels))
            {
                Utils.Warning($"APM bypassed: unsupported channel count={channels}");
                return;
            }

            if (sampleRate == 24000)
            {
                Process24000HzInPlace(pcm, channels, state, processor, writeBack, isReverse);
                return;
            }

            if (!IsSupportedSampleRate(sampleRate))
            {
                Utils.Warning($"APM bypassed: unsupported sample rate={sampleRate}");
                return;
            }

            lock (_lock)
            {
                ThrowIfDisposed();
                ProcessBufferedPcm(pcm, sampleRate, channels, state, processor, writeBack);
            }
        }

        private void Process24000HzInPlace(short[] pcm, int channels, StreamState state, Action<short[], int, int> processor, bool writeBack, bool isReverse)
        {
            var scratchLen = pcm.Length * 2;
            var scratch = isReverse ? _reverseUpsampleScratch : _captureUpsampleScratch;
            if (scratch == null || scratch.Length != scratchLen)
            {
                scratch = new short[scratchLen];
                if (isReverse)
                {
                    _reverseUpsampleScratch = scratch;
                }
                else
                {
                    _captureUpsampleScratch = scratch;
                }
            }

            Upsample2xInterleavedInto(pcm, scratch, channels);
            lock (_lock)
            {
                ThrowIfDisposed();
                ProcessBufferedPcm(scratch, 48000, channels, state, processor, writeBack);
            }
            if (writeBack)
            {
                DownsampleBy2Interleaved(scratch, pcm, channels);
            }
        }

        private static bool IsSupportedSampleRate(int sampleRate)
        {
            return sampleRate == 16000 || sampleRate == 32000 || sampleRate == 48000;
        }

        private static bool IsSupportedChannels(int channels)
        {
            return channels == 1 || channels == 2;
        }

        public static int GetTenMsFrameSampleCount(int sampleRate, int channels)
        {
            if (sampleRate <= 0 || channels <= 0)
            {
                return 0;
            }

            return sampleRate / 100 * channels;
        }

        public static short[] Upsample24kTo48k(short[] input, int channels)
        {
            var output = new short[input.Length * 2];
            Upsample2xInterleavedInto(input, output, channels);
            return output;
        }

        public static void Downsample48kTo24k(short[] input, short[] output, int channels)
        {
            DownsampleBy2Interleaved(input, output, channels);
        }

        private static void Upsample2xInterleavedInto(short[] input, short[] output, int channels)
        {
            var samplesPerChannel = input.Length / channels;
            var outSamplesPerChannel = samplesPerChannel * 2;
            if (samplesPerChannel == 0)
            {
                Array.Clear(output, 0, output.Length);
                return;
            }

            for (var channel = 0; channel < channels; channel++)
            {
                for (var i = 0; i < samplesPerChannel; i++)
                {
                    var current = input[i * channels + channel];
                    short next = current;
                    if (i + 1 < samplesPerChannel)
                    {
                        next = input[(i + 1) * channels + channel];
                    }

                    var outIndex0 = (2 * i) * channels + channel;
                    var outIndex1 = (2 * i + 1) * channels + channel;
                    output[outIndex0] = current;
                    output[outIndex1] = (short)((current + next) / 2);
                }

                if (outSamplesPerChannel > 0)
                {
                    output[(outSamplesPerChannel - 1) * channels + channel] = input[(samplesPerChannel - 1) * channels + channel];
                }
            }
        }

        private static void DownsampleBy2Interleaved(short[] input, short[] output, int channels)
        {
            var outSamplesPerChannel = output.Length / channels;
            for (var channel = 0; channel < channels; channel++)
            {
                for (var i = 0; i < outSamplesPerChannel; i++)
                {
                    var src0 = input[(2 * i) * channels + channel];
                    var src1 = input[(2 * i + 1) * channels + channel];
                    output[i * channels + channel] = (short)((src0 + src1) / 2);
                }
            }
        }

        private void ProcessBufferedPcm(short[] pcm, int sampleRate, int channels, StreamState state, Action<short[], int, int> processor, bool writeBack)
        {
            if (state.SampleRate != sampleRate || state.Channels != channels || state.FrameSamples <= 0)
            {
                state.Reset(sampleRate, channels);
            }

            for (var i = 0; i < pcm.Length; i++)
            {
                state.InputQueue.Enqueue(pcm[i]);
            }

            while (state.InputQueue.Count >= state.FrameSamples)
            {
                for (var i = 0; i < state.FrameSamples; i++)
                {
                    state.FrameBuffer[i] = state.InputQueue.Dequeue();
                }

                processor(state.FrameBuffer, sampleRate, channels);

                if (writeBack)
                {
                    for (var i = 0; i < state.FrameSamples; i++)
                    {
                        state.OutputQueue.Enqueue(state.FrameBuffer[i]);
                    }
                }
            }

            if (!writeBack)
            {
                return;
            }

            // Prime-and-drain strategy: we can only write back processed samples in
            // contiguous pcm.Length blocks, otherwise we would mix raw and processed
            // audio inside the same callback (causing crackling + residual echo that
            // eventually turns into howling). Until APM has produced at least one full
            // callback worth of processed samples, emit silence so the outbound stream
            // stays aligned (loses ~1 callback of audio at track start only).
            if (!state.Primed)
            {
                if (state.OutputQueue.Count >= pcm.Length)
                {
                    state.Primed = true;
                }
                else
                {
                    Array.Clear(pcm, 0, pcm.Length);
                    return;
                }
            }

            if (state.OutputQueue.Count >= pcm.Length)
            {
                for (var i = 0; i < pcm.Length; i++)
                {
                    pcm[i] = state.OutputQueue.Dequeue();
                }
            }
            else
            {
                // Re-prime on underrun (e.g. sample-rate change), avoid emitting raw audio.
                state.Primed = false;
                Array.Clear(pcm, 0, pcm.Length);
            }
        }

        private unsafe void ProcessStreamFrame(short[] frame, int sampleRate, int channels)
        {
            using var request = FFIBridge.Instance.NewRequest<ApmProcessStreamRequest>();
            var process = request.request;
            process.ApmHandle = (ulong)_handle.DangerousGetHandle();
            process.SampleRate = (uint)sampleRate;
            process.NumChannels = (uint)channels;
            process.Size = (uint)(frame.Length * sizeof(short));
            fixed (short* ptr = frame)
            {
                process.DataPtr = (ulong)ptr;
                using var response = request.Send();
                FfiResponse res = response;
                if (res.ApmProcessStream != null && !string.IsNullOrEmpty(res.ApmProcessStream.Error))
                {
                    Utils.Warning($"ApmProcessStream failed: {res.ApmProcessStream.Error}");
                }
            }
        }

        private unsafe void ProcessReverseStreamFrame(short[] frame, int sampleRate, int channels)
        {
            using var request = FFIBridge.Instance.NewRequest<ApmProcessReverseStreamRequest>();
            var process = request.request;
            process.ApmHandle = (ulong)_handle.DangerousGetHandle();
            process.SampleRate = (uint)sampleRate;
            process.NumChannels = (uint)channels;
            process.Size = (uint)(frame.Length * sizeof(short));
            fixed (short* ptr = frame)
            {
                process.DataPtr = (ulong)ptr;
                using var response = request.Send();
                FfiResponse res = response;
                if (res.ApmProcessReverseStream != null && !string.IsNullOrEmpty(res.ApmProcessReverseStream.Error))
                {
                    Utils.Warning($"ApmProcessReverseStream failed: {res.ApmProcessReverseStream.Error}");
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _handle.Dispose();
            _disposed = true;
        }

        ~AudioProcessingModule()
        {
            Dispose(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AudioProcessingModule));
            }
        }
    }
}
