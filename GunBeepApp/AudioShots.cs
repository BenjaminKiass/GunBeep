using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace GunBeep
{
    // ฟังเสียงปืน: อัดเฉพาะเสียงของโปรแกรม PUBG (Process Loopback ของ Windows 10 2004+)
    //   เพลง / Discord / YouTube เป็นคนละโปรแกรม → ไม่ปนเข้ามา
    //   ไม่แตะตัวเกม ไม่อ่านภาพหรือหน่วยความจำเกม — ขอเสียงจาก Windows แบบเดียวกับ OBS "Application Audio Capture"
    //   เปิดแบบเฉพาะเกมไม่ได้ (Windows เก่า) → ฟังเสียงทั้งเครื่องแทน (มีเสียงอื่นปนได้)
    //   ใช้คู่กับการกดยิง: กดค้างแต่ไม่มีเสียงปืนต่อ = แม็กหมด/ยิงทีละนัด → หยุดดึง
    public sealed class AudioShots : IDisposable
    {
        const double FrameMs = 4;          // ความยาวช่วงที่วัดความดัง
        const double RefractoryMs = 40;    // ห่างกันน้อยกว่านี้ = นัดเดียวกัน (ปืนเร็วสุด ~1000 นัด/นาที = 60 ms)
        const double Rise = 2.2;           // ดังกว่าเสียงก่อนหน้ากี่เท่าถึงนับเป็นเสียงปืน
        const double MinLevel = 0.012;     // เบากว่านี้ไม่นับ (เสียงเงียบ/ไกลมาก)

        static readonly Stopwatch clock = Stopwatch.StartNew();
        public static double NowMs => clock.Elapsed.TotalMilliseconds;

        readonly int pid;                  // 0 = ทั้งเครื่อง
        volatile bool disposed;
        Thread thread;
        WasapiLoopbackCapture sysCap;

        int frameLen, inFrame;
        double frameSum, slow, lastOnset = -1e9, h0, h1, h2;
        long onsets;
        double lastOnsetAt = -1e9;

        double loudest;
        // เสียงดังสุดตั้งแต่ถามครั้งก่อน (ไว้ลง log ดูว่าได้ยินเกมอยู่ไหม)
        public double TakeLoudest() { double v = loudest; loudest = 0; return v; }

        public long Onsets => Interlocked.Read(ref onsets);
        public double LastOnsetMs => Volatile.Read(ref lastOnsetAt);
        public int Pid => pid;
        public string Source { get; private set; } = "-";

        public AudioShots(int processId) { pid = processId; }

        public void Start()
        {
            if (pid != 0)
            {
                thread = new Thread(ProcessLoop) { IsBackground = true, Name = "AudioShots" };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
            else StartSystem();
        }

        // ---------------------------------------------------------------- ตรวจเสียงปืน
        // samples = ค่าแอมพลิจูด (−1..1) แบบ interleaved, t0 = เวลา (NowMs) ของตัวอย่างแรก
        void Feed(ReadOnlySpan<float> samples, int channels, int rate, double t0)
        {
            if (frameLen == 0) frameLen = Math.Max(16, (int)(rate * FrameMs / 1000));
            int frames = 0;
            for (int i = 0; i + channels <= samples.Length; i += channels)
            {
                double peak = 0;
                for (int c = 0; c < channels; c++) { double v = Math.Abs(samples[i + c]); if (v > peak) peak = v; }
                frameSum += peak * peak;
                if (++inFrame < frameLen) continue;
                double level = Math.Sqrt(frameSum / frameLen);
                frameSum = 0;
                inFrame = 0;
                double t = t0 + ++frames * FrameMs;
                // เสียงปืน = ดังขึ้นทันทีใน 4 ms เทียบกับ 12 ms ก่อนหน้า (หางเสียงนัดก่อนก็ยังแยกได้)
                //   และต้องดังกว่าระดับเฉลี่ยช่วงหลัง (กันเสียงที่ขึ้นลงเร็วแต่เบา เช่น เสียงเดิน)
                if (level > loudest) loudest = level;
                double prev = (h0 + h1 + h2) / 3;
                if (level > MinLevel && level > prev * Rise && level > slow * 1.4 && t - lastOnset > RefractoryMs)
                {
                    lastOnset = t;
                    Volatile.Write(ref lastOnsetAt, t);
                    Interlocked.Increment(ref onsets);
                }
                h2 = h1; h1 = h0; h0 = level;
                slow += (level - slow) * 0.01;            // ~0.4 วินาที
            }
        }

        // ---------------------------------------------------------------- เสียงเฉพาะ PUBG
        [StructLayout(LayoutKind.Sequential)]
        struct ActivationParams { public int Type; public uint TargetPid; public int Mode; }   // AUDIOCLIENT_ACTIVATION_PARAMS
        [StructLayout(LayoutKind.Sequential)]
        struct PropVariantBlob { public ushort vt, r1, r2, r3; public uint cb; public IntPtr data; }  // PROPVARIANT (VT_BLOB)
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormatEx { public ushort tag, channels; public uint rate, bytesPerSec; public ushort blockAlign, bits, cbSize; }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
        }

        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAgileObject { }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr session);
            void GetBufferSize(out uint frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint frames);
            void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long def, out long min);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr h);
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpc);
            void ReleaseBuffer(uint frames);
            void GetNextPacketSize(out uint frames);
        }

        sealed class Done : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEventSlim Ev = new();
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Ev.Set();
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
            [MarshalAs(UnmanagedType.LPStruct)] Guid iid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        const int Rate = 48000, Ch = 2;

        void ProcessLoop()
        {
            IntPtr pa = IntPtr.Zero, pv = IntPtr.Zero;
            IAudioClient client = null;
            try
            {
                pa = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
                Marshal.StructureToPtr(new ActivationParams { Type = 1 /* PROCESS_LOOPBACK */, TargetPid = (uint)pid, Mode = 0 /* รวมโปรเซสลูก */ }, pa, false);
                pv = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
                Marshal.StructureToPtr(new PropVariantBlob { vt = 65 /* VT_BLOB */, cb = (uint)Marshal.SizeOf<ActivationParams>(), data = pa }, pv, false);

                var done = new Done();
                ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, pv, done, out var op);
                if (!done.Ev.Wait(5000)) throw new TimeoutException("activate timeout");
                op.GetActivateResult(out int hr, out object obj);
                Marshal.ThrowExceptionForHR(hr);
                client = (IAudioClient)obj;

                var fmt = new WaveFormatEx { tag = 1 /* PCM */, channels = Ch, rate = Rate, bits = 16, blockAlign = Ch * 2, bytesPerSec = Rate * Ch * 2 };
                const uint LOOPBACK = 0x20000, EVENTCALLBACK = 0x40000, AUTOCONVERT = 0x80000000, SRC_QUALITY = 0x08000000;
                client.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERT | SRC_QUALITY, 200_000 /* 20 ms */, 0, ref fmt, IntPtr.Zero);
                using var ev = new AutoResetEvent(false);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                var iid = typeof(IAudioCaptureClient).GUID;
                client.GetService(ref iid, out object svc);
                var cap = (IAudioCaptureClient)svc;
                client.Start();
                Source = "PUBG only";
                Log.Info($"audio: listening to PUBG only (pid {pid})");

                var buf = new float[Rate * Ch];
                while (!disposed)
                {
                    ev.WaitOne(100);
                    while (!disposed)
                    {
                        cap.GetNextPacketSize(out uint packet);
                        if (packet == 0) break;
                        cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                        int n = (int)Math.Min(frames * Ch, (uint)buf.Length);
                        if ((flags & 2) != 0 || data == IntPtr.Zero)            // SILENT
                            Array.Clear(buf, 0, n);
                        else
                            for (int i = 0; i < n; i++) buf[i] = Marshal.ReadInt16(data, i * 2) / 32768f;
                        cap.ReleaseBuffer(frames);
                        Feed(buf.AsSpan(0, n), Ch, Rate, NowMs - frames * 1000.0 / Rate);
                    }
                }
                client.Stop();
            }
            catch (Exception ex)
            {
                if (disposed) return;
                Log.Info($"audio: PUBG-only capture failed ({ex.Message}) → whole system");
                StartSystem();
            }
            finally
            {
                if (client != null) Marshal.ReleaseComObject(client);
                if (pv != IntPtr.Zero) Marshal.FreeHGlobal(pv);
                if (pa != IntPtr.Zero) Marshal.FreeHGlobal(pa);
            }
        }

        // ---------------------------------------------------------------- เสียงทั้งเครื่อง (สำรอง)
        void StartSystem()
        {
            if (disposed) return;
            try
            {
                sysCap = new WasapiLoopbackCapture();
                var f = sysCap.WaveFormat;
                int ch = Math.Max(1, f.Channels), rate = f.SampleRate;
                sysCap.DataAvailable += (s, e) =>
                {
                    var span = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
                    Feed(span, ch, rate, NowMs - span.Length / (double)ch * 1000.0 / rate);
                };
                sysCap.StartRecording();
                Source = "whole system";
                Log.Info($"audio: listening to the whole system {rate} Hz x{ch}");
            }
            catch (Exception ex) { Log.Error("audio start", ex); }
        }

        public void Dispose()
        {
            disposed = true;
            try { sysCap?.StopRecording(); } catch { }
            try { sysCap?.Dispose(); } catch { }
            sysCap = null;
        }
    }
}
