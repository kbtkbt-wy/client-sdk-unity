using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiveKit.Internal
{
    internal static class AecBus
    {
        private sealed class ReverseFrameCache
        {
            public short[] Samples = Array.Empty<short>();
            public int SampleRate;
            public int Channels;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, ReverseFrameCache> _latestReverseBySource = new Dictionary<int, ReverseFrameCache>();
        private static AudioProcessingModule _module;
        private static int _moduleSettingsVersion;
        private static int _settingsVersion;
        private static int _captureRefCount;
        private static bool _aecEnabled = true;
        private static bool _nsEnabled = true;
        private static bool _agcEnabled = true;
        private static bool _hpfEnabled = true;
        private static int _streamDelayMs = 80;
        private static int _appliedStreamDelayMs = -1;
        private static short[] _mixScratch = Array.Empty<short>();

        public static bool AecEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _aecEnabled;
                }
            }
            set
            {
                lock (_lock)
                {
                    if (_aecEnabled == value) return;
                    _aecEnabled = value;
                    _settingsVersion++;
                    EnsureModuleState();
                }
            }
        }

        public static bool NsEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _nsEnabled;
                }
            }
            set
            {
                lock (_lock)
                {
                    if (_nsEnabled == value) return;
                    _nsEnabled = value;
                    _settingsVersion++;
                    EnsureModuleState();
                }
            }
        }

        public static bool AgcEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _agcEnabled;
                }
            }
            set
            {
                lock (_lock)
                {
                    if (_agcEnabled == value) return;
                    _agcEnabled = value;
                    _settingsVersion++;
                    EnsureModuleState();
                }
            }
        }

        public static bool HpfEnabled
        {
            get
            {
                lock (_lock)
                {
                    return _hpfEnabled;
                }
            }
            set
            {
                lock (_lock)
                {
                    if (_hpfEnabled == value) return;
                    _hpfEnabled = value;
                    _settingsVersion++;
                    EnsureModuleState();
                }
            }
        }

        public static int StreamDelayMs
        {
            get
            {
                lock (_lock)
                {
                    return _streamDelayMs;
                }
            }
            set
            {
                lock (_lock)
                {
                    var clamped = Math.Max(0, value);
                    if (_streamDelayMs == clamped) return;
                    _streamDelayMs = clamped;
                    // Stream delay does NOT require APM recreation, just re-apply on next frame.
                    TrySetStreamDelayLocked();
                }
            }
        }

        public static void AcquireCapture()
        {
            lock (_lock)
            {
                _captureRefCount++;
                EnsureModuleState();
            }
        }

        public static void ReleaseCapture()
        {
            lock (_lock)
            {
                if (_captureRefCount > 0)
                {
                    _captureRefCount--;
                }
                EnsureModuleState();
            }
        }

        public static void RegisterReverse(object source)
        {
            if (source == null)
            {
                return;
            }

            lock (_lock)
            {
                var sourceId = RuntimeHelpers.GetHashCode(source);
                if (!_latestReverseBySource.ContainsKey(sourceId))
                {
                    _latestReverseBySource[sourceId] = new ReverseFrameCache();
                }
                EnsureModuleState();
            }
        }

        public static void UnregisterReverse(object source)
        {
            if (source == null)
            {
                return;
            }

            lock (_lock)
            {
                var sourceId = RuntimeHelpers.GetHashCode(source);
                _latestReverseBySource.Remove(sourceId);
                EnsureModuleState();
            }
        }

        public static void ProcessCapture(short[] pcm, int sampleRate, int channels)
        {
            lock (_lock)
            {
                EnsureModuleState();
                if (_module == null)
                {
                    return;
                }

                TrySetStreamDelayLocked();
                _module.ProcessStream(pcm, sampleRate, channels);
            }
        }

        public static void ProcessReverse(object source, short[] pcm, int sampleRate, int channels)
        {
            if (source == null || pcm == null || pcm.Length == 0)
            {
                return;
            }

            lock (_lock)
            {
                EnsureModuleState();
                if (_module == null)
                {
                    return;
                }

                var sourceId = RuntimeHelpers.GetHashCode(source);
                if (!_latestReverseBySource.TryGetValue(sourceId, out var cache))
                {
                    return;
                }

                if (cache.Samples.Length != pcm.Length)
                {
                    cache.Samples = new short[pcm.Length];
                }
                Array.Copy(pcm, cache.Samples, pcm.Length);
                cache.SampleRate = sampleRate;
                cache.Channels = channels;

                if (_latestReverseBySource.Count == 1)
                {
                    TrySetStreamDelayLocked();
                    _module.ProcessReverseStream(cache.Samples, sampleRate, channels);
                    return;
                }

                if (_mixScratch.Length != pcm.Length)
                {
                    _mixScratch = new short[pcm.Length];
                }
                else
                {
                    Array.Clear(_mixScratch, 0, _mixScratch.Length);
                }

                var contributorCount = 0;
                foreach (var frame in _latestReverseBySource.Values)
                {
                    if (frame.SampleRate != sampleRate || frame.Channels != channels || frame.Samples.Length != pcm.Length)
                    {
                        continue;
                    }

                    MixInto(_mixScratch, frame.Samples, contributorCount + 1);
                    contributorCount++;
                }

                if (contributorCount == 0)
                {
                    return;
                }

                TrySetStreamDelayLocked();
                _module.ProcessReverseStream(_mixScratch, sampleRate, channels);
            }
        }

        private static void MixInto(short[] destination, short[] source, int contributorCount)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                var sum = destination[i] * (contributorCount - 1) + source[i];
                var averaged = sum / contributorCount;
                if (averaged > short.MaxValue) averaged = short.MaxValue;
                if (averaged < short.MinValue) averaged = short.MinValue;
                destination[i] = (short)averaged;
            }
        }

        private static void EnsureModuleState()
        {
            var shouldCreate = _aecEnabled && _captureRefCount > 0 && _latestReverseBySource.Count > 0;

            if (!shouldCreate)
            {
                if (_module != null)
                {
                    _module.Dispose();
                    _module = null;
                }
                return;
            }

            if (_module != null && _moduleSettingsVersion == _settingsVersion)
            {
                return;
            }

            _module?.Dispose();
            try
            {
                _module = new AudioProcessingModule(
                    echoCancellerEnabled: _aecEnabled,
                    gainControllerEnabled: _agcEnabled,
                    highPassFilterEnabled: _hpfEnabled,
                    noiseSuppressionEnabled: _nsEnabled
                );
                _moduleSettingsVersion = _settingsVersion;
                _appliedStreamDelayMs = -1;
                TrySetStreamDelayLocked();
            }
            catch (Exception e)
            {
                _module = null;
                Utils.Error($"Failed to initialize APM: {e.Message}");
            }
        }

        private static void TrySetStreamDelayLocked()
        {
            if (_module == null)
            {
                return;
            }

            // Only re-issue the FFI call when the delay actually changes. This avoids
            // holding the lock for a round-trip on every audio frame while still
            // applying setter changes immediately instead of being throttled.
            if (_appliedStreamDelayMs == _streamDelayMs)
            {
                return;
            }

            _module.SetStreamDelayMs(_streamDelayMs);
            _appliedStreamDelayMs = _streamDelayMs;
        }
    }
}
