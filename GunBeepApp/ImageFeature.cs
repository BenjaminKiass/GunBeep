using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace GunBeep
{
    // ลักษณะภาพแบบเทียบได้ (ใช้ทั้งสโคปและของแต่ง) — แยกไฟล์ไว้ให้เครื่องมือสร้างข้อมูลในตัวใช้โค้ดเดียวกันเป๊ะ
    public static class ImageFeature
    {
        const int F = 40;

        // ---------------------------------------------------------------- ภาพ → ลักษณะ
        // ขอบภาพ (gradient) ย่อ 40x40 แล้ว normalize — ทนต่อความสว่าง/พื้นหลังโปร่งแสงที่ต่างกัน
        public static float[] Feature(Bitmap src)
        {
            using var small = new Bitmap(F + 2, F + 2, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, new Rectangle(0, 0, F + 2, F + 2));
            }
            var gray = new float[(F + 2) * (F + 2)];
            var data = small.LockBits(new Rectangle(0, 0, F + 2, F + 2), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[data.Stride];
                for (int y = 0; y < F + 2; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    for (int x = 0; x < F + 2; x++)
                        gray[y * (F + 2) + x] = (row[x * 3] + row[x * 3 + 1] + row[x * 3 + 2]) / 765f;
                }
            }
            finally { small.UnlockBits(data); }
            var f = new float[F * F];
            for (int y = 1; y <= F; y++)
                for (int x = 1; x <= F; x++)
                {
                    float gx = gray[y * (F + 2) + x + 1] - gray[y * (F + 2) + x - 1];
                    float gy = gray[(y + 1) * (F + 2) + x] - gray[(y - 1) * (F + 2) + x];
                    f[(y - 1) * F + x - 1] = MathF.Sqrt(gx * gx + gy * gy);
                }
            float mean = f.Average();
            double norm = 0;
            for (int i = 0; i < f.Length; i++) { f[i] -= mean; norm += f[i] * f[i]; }
            norm = Math.Sqrt(norm);
            if (norm > 1e-6) for (int i = 0; i < f.Length; i++) f[i] = (float)(f[i] / norm);
            return f;
        }


        public static Bitmap Crop(Bitmap src, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width < 2 || r.Height < 2) return new Bitmap(2, 2);
            return src.Clone(r, PixelFormat.Format24bppRgb);
        }
    }
}