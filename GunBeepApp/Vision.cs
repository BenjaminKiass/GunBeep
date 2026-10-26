using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GunBeep
{
    // =========================================================================
    //  Vision: ระบบ AI ตรวจจับปืน ของแต่ง และลำกล้อง จากหน้ากระเป๋า (Tab) ใน PUBG
    // =========================================================================

    public static class GdiHelper
    {
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

        public static BitmapSource ToBitmapSource(Bitmap bmp)
        {
            if (bmp == null) return null;
            IntPtr hBmp = bmp.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBmp);
            }
        }
    }

    public class ColorSig
    {
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double Brightness { get; set; }
        public double Variance { get; set; }
        public bool IsEmpty { get; set; }
    }

    public static class ImageFeatures
    {
        // คำนวณ 64-bit dHash (Difference Hash) บนภาพ grayscale 9x8
        public static ulong ComputeDHash(Bitmap src)
        {
            if (src == null) return 0;
            using var small = new Bitmap(9, 8, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, 9, 8);
            }

            // อ่าน grayscale
            byte[] gray = new byte[72];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    var c = small.GetPixel(x, y);
                    gray[y * 9 + x] = (byte)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                }
            }

            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int left = gray[y * 9 + x];
                    int right = gray[y * 9 + x + 1];
                    if (left > right) hash |= (1UL << bit);
                    bit++;
                }
            }
            return hash;
        }

        // วิเคราะห์สีและขอบ (ตรวจจับว่าช่องอุปกรณ์ว่างเปล่าหรือไม่)
        public static bool IsSlotEmpty(Bitmap src)
        {
            if (src == null || src.Width < 10 || src.Height < 10) return true;
            int xStart = (int)(src.Width * 0.15);
            int xEnd = (int)(src.Width * 0.85);
            int yStart = (int)(src.Height * 0.15);
            int yEnd = (int)(src.Height * 0.85);

            int edgeCount = 0;
            double sumBri = 0;
            int total = 0;
            var bris = new List<double>();

            for (int y = yStart; y < yEnd; y++)
            {
                for (int x = xStart; x < xEnd; x++)
                {
                    var p = src.GetPixel(x, y);
                    double bri = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                    sumBri += bri;
                    bris.Add(bri);
                    total++;

                    if (x < xEnd - 1 && y < yEnd - 1)
                    {
                        var pr = src.GetPixel(x + 1, y);
                        var pd = src.GetPixel(x, y + 1);
                        double diffX = Math.Abs(bri - (0.299 * pr.R + 0.587 * pr.G + 0.114 * pr.B));
                        double diffY = Math.Abs(bri - (0.299 * pd.R + 0.587 * pd.G + 0.114 * pd.B));
                        if (diffX + diffY > 15.0) edgeCount++;
                    }
                }
            }

            if (total == 0) return true;
            double avgBri = sumBri / total;
            double variance = bris.Average(b => Math.Pow(b - avgBri, 2));
            double stdDev = Math.Sqrt(variance);

            // กล่องที่ว่างเปล่าใน PUBG จะเป็นพื้นหลังโปร่งแสงสม่ำเสมอ ไร้เส้นขอบและไอคอน
            return edgeCount < 25 && stdDev < 14.0;
        }

        public static ColorSig AnalyzeColor(Bitmap src)
        {
            if (src == null) return new ColorSig { IsEmpty = true };
            double sumR = 0, sumG = 0, sumB = 0, sumBri = 0;
            int total = 0;

            int startX = (int)(src.Width * 0.2);
            int endX = (int)(src.Width * 0.8);
            int startY = (int)(src.Height * 0.2);
            int endY = (int)(src.Height * 0.8);

            var brightnesses = new List<double>();
            for (int y = startY; y < endY; y += 2)
            {
                for (int x = startX; x < endX; x += 2)
                {
                    var c = src.GetPixel(x, y);
                    sumR += c.R;
                    sumG += c.G;
                    sumB += c.B;
                    double bri = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    sumBri += bri;
                    brightnesses.Add(bri);
                    total++;
                }
            }

            if (total == 0) return new ColorSig { IsEmpty = true };
            double avgR = sumR / total;
            double avgG = sumG / total;
            double avgB = sumB / total;
            double avgBri = sumBri / total;
            double variance = brightnesses.Average(b => Math.Pow(b - avgBri, 2));

            return new ColorSig
            {
                R = avgR,
                G = avgG,
                B = avgB,
                Brightness = avgBri,
                Variance = variance,
                IsEmpty = IsSlotEmpty(src)
            };
        }

        public static int HammingDistance(ulong a, ulong b) =>
            BitOperations.PopCount(a ^ b);
    }

    // =========================================================================
    //  ฐานข้อมูลปืน ของแต่ง และลำกล้อง พร้อมตัวคูณแรงดีดจริง
    // =========================================================================

    public class WeaponDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double BaseRecoil { get; set; }
        public double DefaultX { get; set; }
        public bool HasMuzzle { get; set; } = true;
        public bool HasGrip { get; set; } = true;
        public bool HasStock { get; set; }
        public bool HasScope { get; set; } = true;
    }

    public class AttachmentDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double MultY { get; set; }
        public double MultX { get; set; }
    }

    public static class PUBGData
    {
        public static readonly Dictionary<string, WeaponDef> Weapons = new(StringComparer.OrdinalIgnoreCase)
        {
            ["M416"] = new WeaponDef { Id = "M416", Name = "M416", BaseRecoil = 5.0, DefaultX = 0, HasStock = true },
            ["Beryl"] = new WeaponDef { Id = "Beryl", Name = "Beryl M762", BaseRecoil = 15.5, DefaultX = 0, HasStock = false },
            ["AUG"] = new WeaponDef { Id = "AUG", Name = "AUG", BaseRecoil = 6.0, DefaultX = 0, HasStock = false },
            ["ACE32"] = new WeaponDef { Id = "ACE32", Name = "ACE32", BaseRecoil = 8.5, DefaultX = 0, HasStock = true },
            ["SCAR-L"] = new WeaponDef { Id = "SCAR-L", Name = "SCAR-L", BaseRecoil = 5.5, DefaultX = 0, HasStock = false },
            ["AKM"] = new WeaponDef { Id = "AKM", Name = "AKM", BaseRecoil = 9.5, DefaultX = 0, HasGrip = false, HasStock = false },
            ["MP5K"] = new WeaponDef { Id = "MP5K", Name = "MP5K", BaseRecoil = 5.5, DefaultX = 0, HasStock = true },
            ["Vector"] = new WeaponDef { Id = "Vector", Name = "Vector", BaseRecoil = 6.0, DefaultX = 0, HasStock = true },
            ["UMP45"] = new WeaponDef { Id = "UMP45", Name = "UMP45", BaseRecoil = 4.5, DefaultX = 0, HasStock = false },
            ["Mini14"] = new WeaponDef { Id = "Mini14", Name = "Mini14", BaseRecoil = 4.0, DefaultX = 0, HasGrip = false, HasStock = false },
            ["SKS"] = new WeaponDef { Id = "SKS", Name = "SKS", BaseRecoil = 7.0, DefaultX = 0, HasStock = true },
            ["SLR"] = new WeaponDef { Id = "SLR", Name = "SLR", BaseRecoil = 9.0, DefaultX = 0, HasGrip = false, HasStock = true },
            ["Mk12"] = new WeaponDef { Id = "Mk12", Name = "Mk12", BaseRecoil = 4.5, DefaultX = 0, HasStock = false }
        };

        public static readonly Dictionary<string, AttachmentDef> Muzzles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ไม่มี", MultY = 1.0, MultX = 1.0 },
            ["Compensator"] = new AttachmentDef { Id = "Compensator", Name = "Compensator (คอมเพน)", MultY = 0.85, MultX = 0.90 },
            ["MuzzleBrake"] = new AttachmentDef { Id = "MuzzleBrake", Name = "Muzzle Brake (เบรก)", MultY = 0.90, MultX = 0.90 },
            ["FlashHider"] = new AttachmentDef { Id = "FlashHider", Name = "Flash Hider (ลดแสง)", MultY = 0.90, MultX = 0.90 },
            ["Suppressor"] = new AttachmentDef { Id = "Suppressor", Name = "Suppressor (เก็บเสียง)", MultY = 1.00, MultX = 1.00 }
        };

        public static readonly Dictionary<string, AttachmentDef> Grips = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ไม่มี", MultY = 1.0, MultX = 1.0 },
            ["Vertical"] = new AttachmentDef { Id = "Vertical", Name = "Vertical Grip (กริปตรง)", MultY = 0.85, MultX = 1.00 },
            ["Half"] = new AttachmentDef { Id = "Half", Name = "Half Grip (กริปส้ม)", MultY = 0.92, MultX = 0.92 },
            ["Angled"] = new AttachmentDef { Id = "Angled", Name = "Angled Grip (กริปสามเหลี่ยม)", MultY = 1.00, MultX = 0.85 },
            ["Thumb"] = new AttachmentDef { Id = "Thumb", Name = "Thumb Grip (กริปนิ้วโป้ง)", MultY = 0.95, MultX = 1.00 },
            ["Light"] = new AttachmentDef { Id = "Light", Name = "Light Grip (กริปแดง)", MultY = 0.98, MultX = 1.00 },
            ["Laser"] = new AttachmentDef { Id = "Laser", Name = "Laser Sight (เลเซอร์)", MultY = 1.00, MultX = 1.00 }
        };

        public static readonly Dictionary<string, AttachmentDef> Stocks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ไม่มี", MultY = 1.0, MultX = 1.0 },
            ["Tactical"] = new AttachmentDef { Id = "Tactical", Name = "Tactical Stock (พานท้ายแทค)", MultY = 0.95, MultX = 0.95 },
            ["Heavy"] = new AttachmentDef { Id = "Heavy", Name = "Heavy Stock (พานท้ายหนา)", MultY = 0.95, MultX = 0.95 },
            ["CheekPad"] = new AttachmentDef { Id = "CheekPad", Name = "Cheek Pad (รองแก้ม)", MultY = 0.95, MultX = 0.95 }
        };

        public static readonly Dictionary<string, AttachmentDef> Scopes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ศูนย์เปิด / ไม่ติด", MultY = 1.0, MultX = 1.0 },
            ["RedDot"] = new AttachmentDef { Id = "RedDot", Name = "Red Dot", MultY = 1.0, MultX = 1.0 },
            ["Holo"] = new AttachmentDef { Id = "Holo", Name = "Holographic", MultY = 1.0, MultX = 1.0 },
            ["2x"] = new AttachmentDef { Id = "2x", Name = "สโคป 2x", MultY = 1.20, MultX = 1.0 },
            ["3x"] = new AttachmentDef { Id = "3x", Name = "สโคป 3x", MultY = 1.40, MultX = 1.0 },
            ["4x"] = new AttachmentDef { Id = "4x", Name = "สโคป 4x", MultY = 1.65, MultX = 1.0 },
            ["6x"] = new AttachmentDef { Id = "6x", Name = "สโคป 6x", MultY = 2.05, MultX = 1.0 }
        };

        // คำนวณแรงดีดสุทธิ (ปัดทีละ 0.5 ตามที่ LiveLink รับได้)
        public static (double Base, double X) CalculateRecoil(
            double initialBase, double initialX,
            string muzzle, string grip, string stock, string scope)
        {
            double mY = Muzzles.TryGetValue(muzzle ?? "None", out var m) ? m.MultY : 1.0;
            double mX = m != null ? m.MultX : 1.0;

            double gY = Grips.TryGetValue(grip ?? "None", out var g) ? g.MultY : 1.0;
            double gX = g != null ? g.MultX : 1.0;

            double sY = Stocks.TryGetValue(stock ?? "None", out var s) ? s.MultY : 1.0;
            double sX = s != null ? s.MultX : 1.0;

            double scY = Scopes.TryGetValue(scope ?? "None", out var sc) ? sc.MultY : 1.0;
            double scX = sc != null ? sc.MultX : 1.0;

            double finalBase = initialBase * mY * gY * sY * scY;
            double finalX = initialX * mX * gX * sX;

            double snappedBase = Math.Max(0, Math.Round(finalBase * 2.0) / 2.0);
            double snappedX = Math.Round(finalX * 2.0) / 2.0;

            return (snappedBase, snappedX);
        }
    }

    // =========================================================================
    //  พิกัดช่องอุปกรณ์บนจอ (รองรับ 1728x1080 และ 1920x1080 + ปรับ Offset ±px)
    // =========================================================================

    public class SlotBox
    {
        public System.Drawing.Rectangle Weapon { get; set; }
        public System.Drawing.Rectangle Scope { get; set; }
        public System.Drawing.Rectangle Muzzle { get; set; }
        public System.Drawing.Rectangle Grip { get; set; }
        public System.Drawing.Rectangle Mag { get; set; }
        public System.Drawing.Rectangle Stock { get; set; }
    }

    public static class InventoryLayout
    {
        public static (SlotBox Slot1, SlotBox Slot2) GetLayout(string res, int offX, int offY)
        {
            // พิกัดจริงอิงตาม PUBG แท้ในความละเอียดแนวตั้ง 1080p
            // 1728x1080 (จอยืด): base1X = 1230, base1Y = 75
            // 1920x1080 (จอ 16:9): base1X = 1350, base1Y = 75
            bool is1728 = res != "1920x1080";
            int base1X = is1728 ? 1230 : 1350;
            int base1Y = 75;

            int base2X = base1X;
            int base2Y = 320;

            // นำ offset มารวม
            base1X += offX;
            base1Y += offY;
            base2X += offX;
            base2Y += offY;

            SlotBox BuildSlot(int bx, int by) => new SlotBox
            {
                // กล่องชื่อปืน + หมายเลขช่อง [1] หรือ [2]
                Weapon = new System.Drawing.Rectangle(bx, by, 220, 42),
                // ลำกล้อง (Scope) ด้านบนขวาของช่องปืน
                Scope  = new System.Drawing.Rectangle(bx + 255, by + 30, 60, 60),
                // ปลอก/คอมเพน (Muzzle)
                Muzzle = new System.Drawing.Rectangle(bx, by + 115, 55, 55),
                // ด้ามจับ (Grip)
                Grip   = new System.Drawing.Rectangle(bx + 105, by + 115, 55, 55),
                // แม็กกาซีน (Mag)
                Mag    = new System.Drawing.Rectangle(bx + 210, by + 115, 55, 55),
                // พานท้าย (Stock)
                Stock  = new System.Drawing.Rectangle(bx + 315, by + 115, 55, 55),
            };

            return (BuildSlot(base1X, base1Y), BuildSlot(base2X, base2Y));
        }
    }

    // =========================================================================
    //  ระบบจัดการแม่แบบ (Template Manager & Learner)
    // =========================================================================

    public class ItemTemplate
    {
        public string Category { get; set; }   // Weapon, Muzzle, Grip, Stock, Scope
        public string ItemId { get; set; }     // M416, Compensator, Vertical, etc.
        public ulong DHash { get; set; }
        public double AvgR { get; set; }
        public double AvgG { get; set; }
        public double AvgB { get; set; }
        public double Brightness { get; set; }
        public string ImageFile { get; set; }
    }

    public static class TemplateManager
    {
        static readonly string TemplateDir = Path.Combine(Settings.KitDir, "vision", "templates");
        static readonly string JsonFile = Path.Combine(TemplateDir, "templates.json");
        static readonly string ImageDir = Path.Combine(TemplateDir, "images");

        static List<ItemTemplate> customTemplates = new();
        static readonly object gate = new();

        static TemplateManager()
        {
            LoadCustom();
        }

        public static void LoadCustom()
        {
            lock (gate)
            {
                try
                {
                    if (File.Exists(JsonFile))
                    {
                        string json = File.ReadAllText(JsonFile);
                        customTemplates = JsonSerializer.Deserialize<List<ItemTemplate>>(json) ?? new();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("load templates", ex);
                }
            }
        }

        public static void SaveTemplate(string category, string itemId, Bitmap iconBmp)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(TemplateDir);
                    Directory.CreateDirectory(ImageDir);

                    ulong hash = ImageFeatures.ComputeDHash(iconBmp);
                    var color = ImageFeatures.AnalyzeColor(iconBmp);

                    string fileName = $"{category}_{itemId}.png";
                    string filePath = Path.Combine(ImageDir, fileName);
                    iconBmp.Save(filePath, ImageFormat.Png);

                    customTemplates.RemoveAll(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
                                                   t.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase));

                    customTemplates.Add(new ItemTemplate
                    {
                        Category = category,
                        ItemId = itemId,
                        DHash = hash,
                        AvgR = color.R,
                        AvgG = color.G,
                        AvgB = color.B,
                        Brightness = color.Brightness,
                        ImageFile = fileName
                    });

                    var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    File.WriteAllText(JsonFile, JsonSerializer.Serialize(customTemplates, opt));
                    Log.Info($"saved custom vision template: {category} - {itemId}");
                }
                catch (Exception ex)
                {
                    Log.Error("save vision template", ex);
                }
            }
        }

        // ตรวจจับชื่อปืนและหมายเลขช่องจากแถบข้อความชื่อปืน
        public static (string WeaponId, int SlotNumber) DetectWeaponFromTitle(Bitmap titleBmp)
        {
            if (titleBmp == null) return ("None", 0);

            int w = titleBmp.Width;
            int h = titleBmp.Height;
            int whiteCount = 0;
            int minWhiteX = int.MaxValue;
            int maxWhiteX = -1;
            var whiteCols = new HashSet<int>();
            int numBoxWhites = 0;
            int numBoxW = (int)(w * 0.25);

            for (int x = 0; x < w; x++)
            {
                bool colHasWhite = false;
                for (int y = 0; y < h; y++)
                {
                    var p = titleBmp.GetPixel(x, y);
                    if (p.R > 185 && p.G > 185 && p.B > 185)
                    {
                        whiteCount++;
                        colHasWhite = true;
                        if (x < numBoxW) numBoxWhites++;
                        if (x > numBoxW)
                        {
                            minWhiteX = Math.Min(minWhiteX, x);
                            maxWhiteX = Math.Max(maxWhiteX, x);
                        }
                    }
                }
                if (colHasWhite && x > numBoxW) whiteCols.Add(x);
            }

            // ถ้าไม่มีตัวหนังสือสีขาวเลย = ช่องนี้ว่างเปล่า (ไม่มีปืน)
            if (whiteCount < 20 || whiteCols.Count == 0)
            {
                return ("None", 0);
            }

            // ตรวจสอบหมายเลขช่องจากแถวล่างของกล่องเลข [1] หรือ [2]
            int slotNum = 1;
            if (numBoxWhites > 0)
            {
                int bottomRowWhite = 0;
                int bottomY = (int)(h * 0.70);
                for (int x = 0; x < numBoxW; x++)
                {
                    if (bottomY < h)
                    {
                        var pb = titleBmp.GetPixel(x, bottomY);
                        if (pb.R > 180 && pb.G > 180 && pb.B > 180) bottomRowWhite++;
                    }
                }
                if (bottomRowWhite >= 4) slotNum = 2;
                else slotNum = 1;
            }

            int textWidth = maxWhiteX - minWhiteX;

            // ตรวจสอบช่องว่าง (Gap) ระหว่างคำ: "Beryl M762" มีสองคำและยาวมาก
            bool hasSpace = false;
            int midStart = minWhiteX + (int)(textWidth * 0.35);
            int midEnd = minWhiteX + (int)(textWidth * 0.65);
            int emptyColCount = 0;
            for (int x = midStart; x <= midEnd; x++)
            {
                if (!whiteCols.Contains(x)) emptyColCount++;
            }
            if (emptyColCount >= 2) hasSpace = true;

            string gunId = "M416";
            if (hasSpace || textWidth > 60)
            {
                gunId = "Beryl";
            }
            else if (textWidth <= 38)
            {
                gunId = "AUG";
            }
            else if (textWidth <= 48)
            {
                gunId = "ACE32";
            }

            return (gunId, slotNum);
        }

        // ตรวจจับของแต่งที่ติดตั้งอยู่ (เรียกเมื่อ IsSlotEmpty == false)
        public static string MatchEquippedAttachment(string category, Bitmap iconBmp)
        {
            if (iconBmp == null || ImageFeatures.IsSlotEmpty(iconBmp)) return "None";

            ulong hash = ImageFeatures.ComputeDHash(iconBmp);

            lock (gate)
            {
                ItemTemplate best = null;
                int minDistance = int.MaxValue;
                foreach (var t in customTemplates.Where(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase)))
                {
                    int dist = ImageFeatures.HammingDistance(hash, t.DHash);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        best = t;
                    }
                }
                if (best != null && minDistance <= 14)
                {
                    return best.ItemId;
                }
            }

            var color = ImageFeatures.AnalyzeColor(iconBmp);

            switch (category.ToLowerInvariant())
            {
                case "muzzle":
                    if (color.Brightness > 70) return "Compensator";
                    if (color.Brightness < 35) return "Suppressor";
                    if (color.Brightness > 50) return "MuzzleBrake";
                    return "Compensator"; // ค่าเริ่มต้นของแต่งที่มีประโยชน์สูงสุด

                case "grip":
                    if (color.R > color.B + 25 && color.R > 60) return "Half";
                    if (color.R > color.B + 35) return "Light";
                    return "Vertical"; // กริปตรงยอดนิยม

                case "stock":
                    if (color.Brightness > 45) return "Tactical";
                    return "Heavy";

                case "scope":
                    // ตรวจจับเลนส์สโคปและกระจกฟ้าของ Red Dot
                    int w = iconBmp.Width;
                    int h = iconBmp.Height;
                    int blueDominant = 0;
                    int greenDominant = 0;
                    for (int x = (int)(w * 0.2); x < (int)(w * 0.8); x++)
                    {
                        for (int y = (int)(h * 0.2); y < (int)(h * 0.8); y++)
                        {
                            var p = iconBmp.GetPixel(x, y);
                            if (p.B > p.R + 6 && p.B > 35) blueDominant++;
                            if (p.G > p.R + 8 && p.G > 40) greenDominant++;
                        }
                    }

                    if (greenDominant > 15) return "2x";
                    if (color.Brightness > 65) return "3x";
                    return "RedDot";

                default:
                    return "None";
            }
        }
    }

    // =========================================================================
    //  ผลลัพธ์การสแกน (Detection Report)
    // =========================================================================

    public class SlotResult
    {
        public int SlotIndex { get; set; }     // 1 หรือ 2
        public string WeaponName { get; set; } = "ว่างเปล่า";
        public string Muzzle { get; set; } = "None";
        public string Grip { get; set; } = "None";
        public string Stock { get; set; } = "None";
        public string Scope { get; set; } = "None";

        public double InitialBase { get; set; } = 0.0;
        public double InitialX { get; set; } = 0.0;
        public double FinalBase { get; set; } = 0.0;
        public double FinalX { get; set; } = 0.0;

        public Bitmap WeaponBmp { get; set; }
        public Bitmap ScopeBmp { get; set; }
        public Bitmap MuzzleBmp { get; set; }
        public Bitmap GripBmp { get; set; }
        public Bitmap StockBmp { get; set; }

        public BitmapSource WeaponImg => GdiHelper.ToBitmapSource(WeaponBmp);
        public BitmapSource ScopeImg => GdiHelper.ToBitmapSource(ScopeBmp);
        public BitmapSource MuzzleImg => GdiHelper.ToBitmapSource(MuzzleBmp);
        public BitmapSource GripImg => GdiHelper.ToBitmapSource(GripBmp);
        public BitmapSource StockImg => GdiHelper.ToBitmapSource(StockBmp);

        public string Summary
        {
            get
            {
                if (WeaponName == "ว่างเปล่า" || WeaponName == "None") return "ช่องว่างเปล่า";
                var parts = new List<string>();
                if (Muzzle != "None") parts.Add(Muzzle);
                if (Grip != "None") parts.Add(Grip);
                if (Stock != "None") parts.Add(Stock);
                if (Scope != "None") parts.Add(Scope);
                string att = parts.Count > 0 ? string.Join(" · ", parts) : "ปืนเปล่า";
                return $"[{WeaponName}] {att} → Base {FinalBase:0.0}";
            }
        }
    }

    public class DetectionReport
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public SlotResult Slot1 { get; set; }
        public SlotResult Slot2 { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    // =========================================================================
    //  เอนจินสแกนภาพสด (Screen Capture & Scanner)
    // =========================================================================

    public static class VisionEngine
    {
        public static event Action<DetectionReport> Detected;
        public static DetectionReport LastReport { get; private set; }

        static bool scanning;

        public static Bitmap CaptureRect(System.Drawing.Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            try
            {
                var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Error($"capture rect {rect}", ex);
                return null;
            }
        }

        public static async Task<DetectionReport> ScanAsync(Settings cfg)
        {
            if (scanning) return LastReport;
            scanning = true;

            return await Task.Run(() =>
            {
                try
                {
                    var (box1, box2) = InventoryLayout.GetLayout(cfg.VisionRes, cfg.VisionOffsetX, cfg.VisionOffsetY);

                    var res1 = ScanSlot(1, box1);
                    var res2 = ScanSlot(2, box2);

                    var report = new DetectionReport { Success = true };

                    // จัดการการแมปช่อง:
                    // ถ้า Section 1 มีหมายเลข [2] (ผู้ใช้ใส่ปืนในช่อง 2) และ Section 2 ว่างเปล่า
                    if (res1.SlotIndex == 2 && (res2.WeaponName == "ว่างเปล่า" || res2.WeaponName == "None"))
                    {
                        var empty1 = new SlotResult { SlotIndex = 1, WeaponName = "ว่างเปล่า", FinalBase = 0.0 };
                        report.Slot1 = empty1;
                        report.Slot2 = res1;
                    }
                    else
                    {
                        report.Slot1 = res1;
                        report.Slot2 = res2;
                    }

                    LastReport = report;
                    Detected?.Invoke(report);
                    return report;
                }
                catch (Exception ex)
                {
                    Log.Error("vision scan", ex);
                    var err = new DetectionReport { Success = false, Error = ex.Message };
                    LastReport = err;
                    return err;
                }
                finally
                {
                    scanning = false;
                }
            });
        }

        static SlotResult ScanSlot(int defaultSlotIndex, SlotBox box)
        {
            var res = new SlotResult { SlotIndex = defaultSlotIndex };

            res.WeaponBmp = CaptureRect(box.Weapon);
            res.ScopeBmp  = CaptureRect(box.Scope);
            res.MuzzleBmp = CaptureRect(box.Muzzle);
            res.GripBmp   = CaptureRect(box.Grip);
            res.StockBmp  = CaptureRect(box.Stock);

            // 1. ตรวจจับชื่อปืนและหมายเลขช่อง [1] หรือ [2] จากแถบข้อความชื่อปืน
            var (wId, detectedSlot) = TemplateManager.DetectWeaponFromTitle(res.WeaponBmp);
            if (detectedSlot > 0) res.SlotIndex = detectedSlot;

            if (string.Equals(wId, "None", StringComparison.OrdinalIgnoreCase))
            {
                res.WeaponName = "ว่างเปล่า";
                res.Scope = "None";
                res.Muzzle = "None";
                res.Grip = "None";
                res.Stock = "None";
                res.InitialBase = 0.0;
                res.InitialX = 0.0;
                res.FinalBase = 0.0;
                res.FinalX = 0.0;
                return res;
            }

            res.WeaponName = wId;
            var wDef = PUBGData.Weapons.TryGetValue(wId, out var wd)
                ? wd
                : new WeaponDef { Id = wId, Name = wId, BaseRecoil = 7.0 };

            // 2. ตรวจจับช่องอุปกรณ์: เช็คว่าช่องว่างเปล่าหรือไม่ก่อนเสมอ!
            res.Scope  = ImageFeatures.IsSlotEmpty(res.ScopeBmp)  ? "None" : TemplateManager.MatchEquippedAttachment("Scope", res.ScopeBmp);
            res.Muzzle = (!wDef.HasMuzzle || ImageFeatures.IsSlotEmpty(res.MuzzleBmp)) ? "None" : TemplateManager.MatchEquippedAttachment("Muzzle", res.MuzzleBmp);
            res.Grip   = (!wDef.HasGrip   || ImageFeatures.IsSlotEmpty(res.GripBmp))   ? "None" : TemplateManager.MatchEquippedAttachment("Grip", res.GripBmp);
            res.Stock  = (!wDef.HasStock  || ImageFeatures.IsSlotEmpty(res.StockBmp))  ? "None" : TemplateManager.MatchEquippedAttachment("Stock", res.StockBmp);

            res.InitialBase = wDef.BaseRecoil;
            res.InitialX = wDef.DefaultX;

            var (fBase, fX) = PUBGData.CalculateRecoil(wDef.BaseRecoil, wDef.DefaultX, res.Muzzle, res.Grip, res.Stock, res.Scope);
            res.FinalBase = fBase;
            res.FinalX = fX;

            return res;
        }
    }
}
