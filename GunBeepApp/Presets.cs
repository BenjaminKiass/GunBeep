using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GunBeep
{
    // โปรไฟล์ค่าปืน = ชื่อปืน + base/x ทั้ง 3 ชุด เก็บเป็นไฟล์ JSON ใน saved\presets (ลง Windows ใหม่ไม่หาย)
    public class Preset
    {
        public string App { get; set; } = "GunBeep";
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public DateTime Saved { get; set; } = DateTime.Now;
        public List<PresetGun> Guns { get; set; } = new();

        public class PresetGun
        {
            public string Name { get; set; } = "";
            public double Base { get; set; }
            public double X { get; set; }
            public List<double[]> Segs { get; set; }    // กราฟแรงดีด {แรงดึง, จำนวนช่วง 10 ms} (ไม่มี = ไฟล์รุ่นเก่า)

            public double[] StepArray() =>
                Segs == null || Segs.Count == 0 ? null : GHub.Expand(Segs.Select(p => (p[0], (int)p[1])));
        }

        public static readonly string Dir = Path.Combine(Settings.KitDir, "presets");

        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true, PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static Preset From(string name, string[] names, GHub.GunVals[] vals) => new()
        {
            Name = name,
            Guns = vals.Select((v, i) => new PresetGun
            {
                Name = names[i], Base = v.Base, X = v.X,
                Segs = v.Steps == null ? null : GHub.Compress(v.Steps).Select(p => new[] { p.v, (double)p.n }).ToList()
            }).ToList()
        };

        // ค่าตรงกับที่ใช้อยู่ไหม (ไว้โชว์ว่าแก้ไปจากโปรไฟล์แล้ว)
        public bool Matches(string[] names, GHub.GunVals[] vals) =>
            vals != null && Guns.Count == vals.Length &&
            Guns.Select((g, i) => g.Name == names[i] && Math.Abs(g.Base - vals[i].Base) < 1e-9 && Math.Abs(g.X - vals[i].X) < 1e-9
                                  && (g.Segs == null || LiveLink.SameGraph(g.StepArray(), vals[i].Steps))).All(b => b);

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        // อ่าน + ตรวจว่าเป็นไฟล์ของ GunBeep ค่าอยู่ในช่วงที่สมเหตุสมผล
        public static Preset Parse(string text)
        {
            var p = JsonSerializer.Deserialize<Preset>(text, Json);
            if (p == null || p.Guns == null || p.Guns.Count < 1 || p.Guns.Count > 3)
                throw new InvalidDataException("ไม่ใช่ไฟล์โปรไฟล์ของ GunBeep");
            foreach (var g in p.Guns)
            {
                if (double.IsNaN(g.Base) || g.Base < 0 || g.Base > 60 || double.IsNaN(g.X) || g.X < -30 || g.X > 30)
                    throw new InvalidDataException("ค่า base/x ในไฟล์ผิดปกติ");
                g.Base = Math.Round(g.Base * 2) / 2;
                g.X = Math.Round(g.X * 2) / 2;
                if (g.Segs != null)
                {
                    if (g.Segs.Any(p => p == null || p.Length != 2 || double.IsNaN(p[0]) || p[0] < 0 || p[0] > 50
                                         || p[1] < 1 || p[1] > 2000 || p[1] != Math.Floor(p[1]))
                        || g.Segs.Sum(p => p[1]) > 2000)
                        throw new InvalidDataException("กราฟแรงดีดในไฟล์ผิดปกติ");
                    if (g.Segs.Count == 0) g.Segs = null;
                }
                g.Name = (g.Name ?? "").Trim();
                if (g.Name.Length > 14) g.Name = g.Name.Substring(0, 14);
            }
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "นำเข้า" : p.Name.Trim();
            return p;
        }

        public static Preset Load(string path) => Parse(File.ReadAllText(path));

        public static List<Preset> List()
        {
            var list = new List<Preset>();
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                try { list.Add(Load(f)); } catch { }
            }
            return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string PathFor(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            string safe = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (safe.Length == 0) safe = "preset";
            return Path.Combine(Dir, safe + ".json");
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            Saved = DateTime.Now;
            File.WriteAllText(PathFor(Name), ToJson());
        }

        public static void Delete(string name)
        {
            string p = PathFor(name);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // กล่องพิมพ์ชื่อ (ธีมเดียวกับหน้าต่างหลัก)
    public class InputDialog : Window
    {
        readonly TextBox box;

        InputDialog(string title, string prompt, string initial)
        {
            Title = title;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI, Leelawadee UI");

            var text = new TextBlock
            {
                Text = prompt, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFF)), Margin = new Thickness(0, 0, 0, 10)
            };
            box = new TextBox
            {
                Text = initial, FontSize = 14, MaxLength = 40, Width = 300, Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x16)), Foreground = Brushes.White,
                CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xFF)),
                BorderThickness = new Thickness(1.5)
            };
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) Done(true);
                else if (e.Key == Key.Escape) Done(false);
            };

            var ok = Btn("ตกลง", true);
            var cancel = Btn("ยกเลิก", false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var stack = new StackPanel();
            stack.Children.Add(text);
            stack.Children.Add(box);
            stack.Children.Add(buttons);

            Content = new Border
            {
                Margin = new Thickness(16), Padding = new Thickness(20, 16, 20, 16), CornerRadius = new CornerRadius(16),
                Background = new LinearGradientBrush(Color.FromRgb(0x0E, 0x15, 0x28), Color.FromRgb(0x07, 0x0A, 0x12), 90),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xFF)), BorderThickness = new Thickness(1.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.6 },
                Child = stack
            };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
        }

        Button Btn(string label, bool result)
        {
            var b = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), IsDefault = result, IsCancel = !result };
            if (Application.Current.MainWindow?.TryFindResource("KeyBtn") is Style st) b.Style = st;
            b.Click += (s, e) => Done(result);
            return b;
        }

        void Done(bool ok)
        {
            DialogResult = ok && box.Text.Trim().Length > 0;
        }

        public static string Ask(Window owner, string title, string prompt, string initial = "")
        {
            var d = new InputDialog(title, prompt, initial) { Owner = owner };
            return d.ShowDialog() == true ? d.box.Text.Trim() : null;
        }
    }
}
