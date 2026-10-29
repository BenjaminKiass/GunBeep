using System;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GunBeep
{
    // ฟังเสียงปืนจากลำโพง/หูฟัง (เสียงที่ Windows ส่งออก แบบเดียวกับโปรแกรมอัดจอ/Discord)
    //   ไม่แตะตัวเกม ไม่อ่านภาพหรือหน่วยความจำเกม — ดูแค่ "เสียงดังกระแทกขึ้นมาทันที" (onset)
    //   ใช้คู่กับการกดยิง: กดค้างแต่ไม่มีเสียงปืนต่อ = แม็กหมด/ยิงทีละนัด → หยุดดึง
    public sealed class AudioShots : IDisposable
    {
        const double FrameMs = 4;          // ความยาวช่วงที่วัดความดัง
        const double RefractoryMs = 40;    // ห่างกันน้อยกว่านี้ = นัดเดียวกัน (ปืนเร็วสุด ~1000 นัด/นาที = 60 ms)
        const double Rise = 2.2;           // ดังกว่าเสียงก่อนหน้ากี่เท่าถึงนับเป็นเสียงปืน
        const double MinLevel = 0.012;     // เบากว่านี้ไม่นับ (เสียงเงียบ/ไกลมาก)

        static readonly Stopwatch clock = Stopwatch.StartNew();
        public static double NowMs => clock.Elapsed.TotalMilliseconds;

        WasapiLoopbackCapture cap;
        int channels, frameLen, inFrame;
        double frameSum, slow, lastOnset = -1e9, h0, h1, h2;
        long onsets;
        double lastOnsetAt = -1e9;          // เวลา (NowMs) ของเสียงปืนล่าสุด
        double lastDataAt = -1e9;
        volatile bool disposed;

        public long Onsets => Interlocked.Read(ref onsets);
        public double LastOnsetMs => Volatile.Read(ref lastOnsetAt);
        public bool Running => cap != null && NowMs - Volatile.Read(ref lastDataAt) < 3000;

        public void Start()
        {
            if (disposed) return;
            try
            {
                cap = new WasapiLoopbackCapture();
                var f = cap.WaveFormat;
                channels = Math.Max(1, f.Channels);
                frameLen = Math.Max(16, (int)(f.SampleRate * FrameMs / 1000));
                if (f.Encoding != WaveFormatEncoding.IeeeFloat && !(f is WaveFormatExtensible && f.BitsPerSample == 32))
                    Log.Info($"audio: unexpected format {f}");
                cap.DataAvailable += OnData;
                cap.RecordingStopped += (s, e) =>
                {
                    // เปลี่ยนลำโพง/หูฟัง → เริ่มฟังอุปกรณ์ใหม่
                    if (e.Exception != null) Log.Info("audio stopped: " + e.Exception.Message);
                    var old = cap;
                    cap = null;
                    try { old?.Dispose(); } catch { }
                    if (!disposed) new Timer(_ => Start(), null, 2000, Timeout.Infinite);
                };
                cap.StartRecording();
                Log.Info($"audio: listening {f.SampleRate} Hz x{channels}");
            }
            catch (Exception ex)
            {
                Log.Error("audio start", ex);
                cap = null;
                if (!disposed) new Timer(_ => Start(), null, 10000, Timeout.Infinite);
            }
        }

        void OnData(object sender, WaveInEventArgs e)
        {
            Volatile.Write(ref lastDataAt, NowMs);
            int samples = e.BytesRecorded / 4;
            double now = NowMs;
            // เวลาของตัวอย่างแรกใน buffer โดยประมาณ (buffer มาช้ากว่าเสียงจริงเท่าความยาวของมัน)
            double bufMs = samples / (double)channels / (frameLen / FrameMs);
            double t0 = now - bufMs;
            int frames = 0;
            for (int i = 0; i + channels <= samples; i += channels)
            {
                double peak = 0;
                for (int c = 0; c < channels; c++)
                {
                    double v = Math.Abs(BitConverter.ToSingle(e.Buffer, (i + c) * 4));
                    if (v > peak) peak = v;
                }
                frameSum += peak * peak;
                if (++inFrame < frameLen) continue;
                double level = Math.Sqrt(frameSum / frameLen);
                frameSum = 0;
                inFrame = 0;
                double t = t0 + ++frames * FrameMs;
                // เสียงปืน = ดังขึ้นทันทีใน 4 ms เทียบกับ 12 ms ก่อนหน้า (หางเสียงนัดก่อนก็ยังแยกได้)
                //   และต้องดังกว่าระดับเฉลี่ยช่วงหลัง (กันเสียงพูด/เพลงที่ขึ้นลงเร็วแต่เบา)
                double prev = (h0 + h1 + h2) / 3;
                bool hit = level > MinLevel && level > prev * Rise && level > slow * 1.4 && t - lastOnset > RefractoryMs;
                if (hit)
                {
                    lastOnset = t;
                    Volatile.Write(ref lastOnsetAt, t);
                    Interlocked.Increment(ref onsets);
                }
                h2 = h1; h1 = h0; h0 = level;
                slow += (level - slow) * 0.01;            // ~0.4 วินาที
            }
        }

        public void Dispose()
        {
            disposed = true;
            try { cap?.StopRecording(); } catch { }
            try { cap?.Dispose(); } catch { }
            cap = null;
        }
    }
}
