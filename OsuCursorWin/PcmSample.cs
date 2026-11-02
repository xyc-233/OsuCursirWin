using System;
using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;

namespace OsuCursorWin;

internal sealed class PcmSample
{
    private readonly byte[] _pcm;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _bitsPerSample;

    internal PcmSample(byte[] wavBytes)
    {
        ParseWave(wavBytes, out _pcm, out _channels, out _sampleRate, out _bitsPerSample);
    }

    internal WaveStream CreateStream(double frequency, double volume, double balance)
    {
        var outputRate = (int)Math.Clamp(Math.Round(_sampleRate * frequency), 8000.0, 96000.0);
        return new PcmWaveStream(BuildPcm(volume, balance), new WaveFormat(outputRate, _bitsPerSample, _channels));
    }

    private sealed class PcmWaveStream : RawSourceWaveStream
    {
        private readonly MemoryStream _source;

        internal PcmWaveStream(byte[] pcm, WaveFormat format)
            : this(new MemoryStream(pcm, false), format)
        {
        }

        private PcmWaveStream(MemoryStream source, WaveFormat format)
            : base(source, format)
        {
            _source = source;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _source.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private byte[] BuildPcm(double volume, double balance)
    {
        var bytesPerSample = _bitsPerSample / 8;
        var frameCount = _pcm.Length / (bytesPerSample * _channels);
        var output = new byte[_pcm.Length];

        var pan = Math.Clamp(balance, -1.0, 1.0);
        var leftGain = volume * Math.Sqrt(Math.Max(0.0, 1.0 - pan));
        var rightGain = volume * Math.Sqrt(Math.Max(0.0, 1.0 + pan));

        for (var i = 0; i < frameCount; i++)
        {
            var offset = i * bytesPerSample * _channels;

            if (_channels == 2)
            {
                var left = ReadSample(offset);
                var right = ReadSample(offset + bytesPerSample);
                WriteSample(output, offset, left * leftGain);
                WriteSample(output, offset + bytesPerSample, right * rightGain);
            }
            else
            {
                var sample = ReadSample(offset);
                WriteSample(output, offset, sample * volume);
            }
        }

        return output;
    }

    private short ReadSample(int offset)
    {
        return _bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(_pcm.AsSpan(offset, 2)),
            8 => (short)((_pcm[offset] - 128) * 256),
            _ => throw new NotSupportedException($"Unsupported WAV bit depth: {_bitsPerSample}")
        };
    }

    private void WriteSample(byte[] output, int offset, double value)
    {
        if (_bitsPerSample == 8)
        {
            output[offset] = (byte)Math.Clamp(Math.Round(value / 256.0) + 128.0, 0.0, 255.0);
            return;
        }

        var sample = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(offset, 2), sample);
    }

    private static void ParseWave(byte[] data, out byte[] pcm, out int channels, out int sampleRate, out int bitsPerSample)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12
            || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "RIFF"
            || System.Text.Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a valid RIFF WAVE file.");
        }

        channels = 0;
        sampleRate = 0;
        bitsPerSample = 0;
        pcm = Array.Empty<byte>();

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        if (riffSize < 4 || riffSize > data.Length - 8)
        {
            throw new InvalidDataException("Invalid RIFF chunk length.");
        }

        var offset = 12;
        var riffEnd = (int)riffSize + 8;
        var pcmOffset = 0;
        var pcmLength = 0;
        while (offset < riffEnd)
        {
            if (riffEnd - offset < 8)
            {
                throw new InvalidDataException("Truncated WAV chunk header.");
            }

            var chunkId = System.Text.Encoding.ASCII.GetString(data, offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            var chunkData = offset + 8;
            var paddedSize = (long)chunkSize + (chunkSize & 1);
            if (paddedSize > riffEnd - chunkData)
            {
                throw new InvalidDataException("Invalid WAV chunk length.");
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("Truncated WAV format chunk.");
                }

                var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData, 2));
                if (audioFormat != 1)
                {
                    throw new NotSupportedException($"Unsupported WAV format: {audioFormat}");
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(chunkData + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData + 14, 2));
            }
            else if (chunkId == "data")
            {
                pcmOffset = chunkData;
                pcmLength = (int)chunkSize;
            }

            offset = chunkData + (int)paddedSize;
        }

        if (channels == 0 || sampleRate <= 0 || bitsPerSample == 0 || pcmLength == 0)
        {
            throw new InvalidDataException("WAV file is missing required chunks.");
        }

        if (channels is not (1 or 2) || bitsPerSample is not (8 or 16))
        {
            throw new NotSupportedException($"Unsupported WAV format: {channels} channels, {bitsPerSample} bits.");
        }

        if (pcmLength % (channels * (bitsPerSample / 8)) != 0)
        {
            throw new InvalidDataException("WAV data contains an incomplete sample frame.");
        }

        pcm = data.AsSpan(pcmOffset, pcmLength).ToArray();
    }
}
