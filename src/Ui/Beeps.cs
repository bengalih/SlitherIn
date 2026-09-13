using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SlitherIn.Core.Config;

namespace SlitherIn.Ui
{
    /// <summary>
    /// MCI tone/file playback for toggle.Sound cues (arm/disarm, chunk). Lives
    /// OUTSIDE Core deliberately — it needs P/Invoke, and Core stays P/Invoke-free
    /// by rule so the test project can reference Core alone.
    ///
    /// Tones are synthesized as a square-wave WAV into the temp dir then played
    /// through MCI; files (wav/mp3) play directly. A single fixed alias means
    /// <see cref="Cancel"/> reliably cuts whatever is currently sounding.
    /// </summary>
    public static class Beeps
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        private static extern int mciSendString(string command, StringBuilder buffer, int bufferLength, IntPtr callback);

        private const string Alias = "slitherin_audio";
        private static readonly string TonePath = Path.Combine(Path.GetTempPath(), "slitherin-tone.wav");

        /// <summary>Play a square-ish tone (default 500 Hz / 1 s matches the old
        /// Sound.cs default; 0/Negative falls back to the default).</summary>
        public static void Tone(int frequency, int durationMs)
        {
            if (frequency <= 0) frequency = 500;
            if (durationMs <= 0) durationMs = 1000;
            File.WriteAllBytes(TonePath, SquareWaveWav(frequency, durationMs));
            PlayFile(TonePath);
        }

        /// <summary>Play a wav/mp3 file through MCI (open + play on the alias).</summary>
        public static void PlayFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            Cancel();
            mciSendString($"open \"{path}\" alias {Alias}", null, 0, IntPtr.Zero);
            mciSendString($"play {Alias}", null, 0, IntPtr.Zero);
        }

        /// <summary>Cut any playing cue (cancellable like everything else). Also
        /// called before each new cue so cues never overlap.</summary>
        public static void Cancel()
        {
            mciSendString($"stop {Alias}", null, 0, IntPtr.Zero);
            mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
        }

        /// <summary>Play a cue spec: file wins; otherwise a frequency/duration tone
        /// (a present-but-empty spec plays the default tone — "present slot beeps").</summary>
        public static void PlayCue(SoundSpec spec)
        {
            if (spec == null) return;
            if (!string.IsNullOrWhiteSpace(spec.File)) { PlayFile(spec.File); return; }
            Tone(spec.Frequency ?? 500, spec.DurationMs ?? 1000);
        }

        /// <summary>16-bit mono PCM square wave at 8 kHz, RFC-ly wrapped — precise
        /// enough for a UI cue and avoids any audio-codec dependency in the app.</summary>
        private static byte[] SquareWaveWav(int frequency, int durationMs)
        {
            const int sampleRate = 8000;
            int samples = sampleRate * durationMs / 1000;
            int period = Math.Max(2, sampleRate / Math.Max(1, frequency));

            using var ms = new MemoryStream(44 + samples * 2);
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + samples * 2);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);              // PCM
                bw.Write((short)1);              // mono
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2);        // byte rate
                bw.Write((short)2);              // block align
                bw.Write((short)16);             // bits per sample
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(samples * 2);

                for (int i = 0; i < samples; i++)
                    bw.Write((short)((i % period) < (period / 2) ? 8000 : -8000));
            }
            return ms.ToArray();
        }
    }
}