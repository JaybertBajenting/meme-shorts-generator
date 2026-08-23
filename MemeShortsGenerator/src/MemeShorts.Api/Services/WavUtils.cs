namespace MemeShorts.Api.Services;

/// <summary>Minimal RIFF/WAVE reader — just enough to get a clip's duration.</summary>
public static class WavUtils
{
    public static int GetDurationMs(string wavPath)
    {
        using var fs = File.OpenRead(wavPath);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") return 0;
        br.ReadInt32();                       // riff chunk size
        if (new string(br.ReadChars(4)) != "WAVE") return 0;

        int byteRate = 0;
        long dataBytes = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            var chunkId = new string(br.ReadChars(4));
            int chunkSize = br.ReadInt32();
            long chunkStart = fs.Position;

            if (chunkId == "fmt ")
            {
                br.ReadInt16();               // audio format
                br.ReadInt16();               // channels
                br.ReadInt32();               // sample rate
                byteRate = br.ReadInt32();    // bytes/sec — gives us duration directly
            }
            else if (chunkId == "data")
            {
                dataBytes = chunkSize;
            }

            // chunks are word-aligned
            long next = chunkStart + chunkSize + (chunkSize & 1);
            if (next <= chunkStart) break;
            fs.Position = next;
        }

        if (byteRate <= 0 || dataBytes <= 0) return 0;
        return (int)Math.Round(dataBytes * 1000.0 / byteRate);
    }

    /// <summary>Peak/clipping stats for a 16-bit PCM WAV. Used to catch distorted TTS takes.</summary>
    public readonly record struct PeakStats(int TotalSamples, int ClippedSamples, int MaxClipRun, int PeakAbs)
    {
        public double ClipRatio => TotalSamples > 0 ? (double)ClippedSamples / TotalSamples : 0;
        public double PeakDb => PeakAbs > 0 ? 20.0 * Math.Log10(PeakAbs / 32768.0) : double.NegativeInfinity;
    }

    /// <summary>
    /// Scan a 16-bit PCM WAV for hard clipping: counts full-scale samples and the longest run of
    /// consecutive near-max samples (a flat-topped waveform is the signature of real distortion).
    /// </summary>
    public static PeakStats AnalyzePeaks(string wavPath)
    {
        const int nearFull = 32760; // within ~0.02 dB of full scale
        using var fs = File.OpenRead(wavPath);
        using var br = new BinaryReader(fs);
        if (new string(br.ReadChars(4)) != "RIFF") return default;
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") return default;

        int total = 0, clipped = 0, run = 0, maxRun = 0, peak = 0;
        while (fs.Position + 8 <= fs.Length)
        {
            var chunkId = new string(br.ReadChars(4));
            int chunkSize = br.ReadInt32();
            long chunkStart = fs.Position;

            if (chunkId == "data")
            {
                long end = Math.Min(fs.Length, chunkStart + chunkSize);
                while (fs.Position + 2 <= end)
                {
                    int s = br.ReadInt16();
                    int a = Math.Abs(s);
                    total++;
                    if (a > peak) peak = a;
                    if (a >= nearFull) { clipped++; run++; if (run > maxRun) maxRun = run; }
                    else run = 0;
                }
            }

            long next = chunkStart + chunkSize + (chunkSize & 1);
            if (next <= chunkStart) break;
            fs.Position = next;
        }
        return new PeakStats(total, clipped, maxRun, peak);
    }

    /// <summary>Wrap raw little-endian PCM samples in a minimal WAV container.</summary>
    public static void WritePcmAsWav(byte[] pcm, string path, int sampleRate, int channels, int bitsPerSample)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + pcm.Length);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);                 // PCM fmt chunk size
        bw.Write((short)1);           // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write("data".ToCharArray());
        bw.Write(pcm.Length);
        bw.Write(pcm);
    }
}
