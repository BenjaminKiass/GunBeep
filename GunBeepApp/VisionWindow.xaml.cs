using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GunBeep
{
    public partial class VisionWindow : Window
    {
        readonly Settings cfg;
        readonly Action<int, double, double, string> onApplyRecoil;
        DetectionReport currentReport;

        public VisionWindow(Settings cfg, Action<int, double, double, string> onApplyRecoil)
        {
            this.cfg = cfg;
            this.onApplyRecoil = onApplyRecoil;
            InitializeComponent();

            SliderOffX.Value = cfg.VisionOffsetX;
            SliderOffY.Value = cfg.VisionOffsetY;
            TxtOffX.Text = (cfg.VisionOffsetX >= 0 ? "+" : "") + cfg.VisionOffsetX + " px";
            TxtOffY.Text = (cfg.VisionOffsetY >= 0 ? "+" : "") + cfg.VisionOffsetY + " px";

            AutoTabSwitch.IsChecked = cfg.VisionAuto;
            AutoApplySwitch.IsChecked = cfg.VisionAutoApply;

            UpdateResButtons();
            PopulateLearnItems(0);

            if (VisionEngine.LastReport != null)
            {
                RenderReport(VisionEngine.LastReport);
            }

            Loaded += async (s, e) =>
            {
                await ScanNowAsync();
            };
        }

        void Title_Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void UpdateResButtons()
        {
            bool is1728 = cfg.VisionRes == "1728x1080";
            Res1728Btn.BorderBrush = is1728 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x6B, 0xFF)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x34, 0x5A));
            Res1920Btn.BorderBrush = !is1728 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x6B, 0xFF)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x34, 0x5A));
        }

        void Res1728_Click(object sender, RoutedEventArgs e)
        {
            cfg.VisionRes = "1728x1080";
            cfg.Save();
            UpdateResButtons();
            _ = ScanNowAsync();
        }

        void Res1920_Click(object sender, RoutedEventArgs e)
        {
            cfg.VisionRes = "1920x1080";
            cfg.Save();
            UpdateResButtons();
            _ = ScanNowAsync();
        }

        void Offset_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtOffX == null || TxtOffY == null) return;
            int ox = (int)SliderOffX.Value;
            int oy = (int)SliderOffY.Value;
            cfg.VisionOffsetX = ox;
            cfg.VisionOffsetY = oy;
            cfg.Save();

            TxtOffX.Text = (ox >= 0 ? "+" : "") + ox + " px";
            TxtOffY.Text = (oy >= 0 ? "+" : "") + oy + " px";
        }

        void ResetOffset_Click(object sender, RoutedEventArgs e)
        {
            SliderOffX.Value = 0;
            SliderOffY.Value = 0;
        }

        void Settings_Changed(object sender, RoutedEventArgs e)
        {
            cfg.VisionAuto = AutoTabSwitch.IsChecked == true;
            cfg.VisionAutoApply = AutoApplySwitch.IsChecked == true;
            cfg.Save();
        }

        async void ScanNow_Click(object sender, RoutedEventArgs e)
        {
            await ScanNowAsync();
        }

        public async System.Threading.Tasks.Task ScanNowAsync()
        {
            ScanNowBtn.IsEnabled = false;
            StatusMsg.Text = "กำลังสแกนหน้าจอ...";
            try
            {
                var rep = await VisionEngine.ScanAsync(cfg);
                currentReport = rep;
                RenderReport(rep);
                StatusMsg.Text = rep.Success ? $"สแกนสำเร็จเมื่อ {DateTime.Now:HH:mm:ss}" : "สแกนไม่สำเร็จ: " + rep.Error;
            }
            finally
            {
                ScanNowBtn.IsEnabled = true;
            }
        }

        public void RenderReport(DetectionReport rep)
        {
            if (rep == null) return;
            currentReport = rep;

            // Slot 1
            if (rep.Slot1 != null)
            {
                var s1 = rep.Slot1;
                S1RecoilBadge.Text = $"Base {s1.FinalBase:0.0} · X {s1.FinalX:0.0}";
                S1TxtWeapon.Text = s1.WeaponName;
                S1TxtScope.Text = s1.Scope;
                S1TxtMuzzle.Text = s1.Muzzle;
                S1TxtGrip.Text = s1.Grip;
                S1TxtStock.Text = s1.Stock;

                S1ImgWeapon.Source = s1.WeaponImg;
                S1ImgScope.Source = s1.ScopeImg;
                S1ImgMuzzle.Source = s1.MuzzleImg;
                S1ImgGrip.Source = s1.GripImg;
                S1ImgStock.Source = s1.StockImg;

                S1Formula.Text = $"{s1.WeaponName} ({s1.InitialBase:0.0}) + {s1.Muzzle} + {s1.Grip} + {s1.Stock} + {s1.Scope} → Base {s1.FinalBase:0.0}";
            }

            // Slot 2
            if (rep.Slot2 != null)
            {
                var s2 = rep.Slot2;
                S2RecoilBadge.Text = $"Base {s2.FinalBase:0.0} · X {s2.FinalX:0.0}";
                S2TxtWeapon.Text = s2.WeaponName;
                S2TxtScope.Text = s2.Scope;
                S2TxtMuzzle.Text = s2.Muzzle;
                S2TxtGrip.Text = s2.Grip;
                S2TxtStock.Text = s2.Stock;

                S2ImgWeapon.Source = s2.WeaponImg;
                S2ImgScope.Source = s2.ScopeImg;
                S2ImgMuzzle.Source = s2.MuzzleImg;
                S2ImgGrip.Source = s2.GripImg;
                S2ImgStock.Source = s2.StockImg;

                S2Formula.Text = $"{s2.WeaponName} ({s2.InitialBase:0.0}) + {s2.Muzzle} + {s2.Grip} + {s2.Stock} + {s2.Scope} → Base {s2.FinalBase:0.0}";
            }
        }

        void ApplySlot1_Click(object sender, RoutedEventArgs e)
        {
            if (currentReport?.Slot1 == null) return;
            var s = currentReport.Slot1;
            onApplyRecoil?.Invoke(1, s.FinalBase, s.FinalX, s.Summary);
            StatusMsg.Text = $"ใช้ค่า {s.Summary} กับปืน 1 แล้ว";
        }

        void ApplySlot2_Click(object sender, RoutedEventArgs e)
        {
            if (currentReport?.Slot2 == null) return;
            var s = currentReport.Slot2;
            onApplyRecoil?.Invoke(2, s.FinalBase, s.FinalX, s.Summary);
            StatusMsg.Text = $"ใช้ค่า {s.Summary} กับปืน 2 แล้ว";
        }

        void LearnCat_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LearnItemCmb == null) return;
            PopulateLearnItems(LearnCatCmb.SelectedIndex);
        }

        void PopulateLearnItems(int catIndex)
        {
            LearnItemCmb.Items.Clear();
            string[] items = catIndex switch
            {
                0 => PUBGData.Weapons.Keys.ToArray(),
                1 => PUBGData.Muzzles.Keys.ToArray(),
                2 => PUBGData.Grips.Keys.ToArray(),
                3 => PUBGData.Stocks.Keys.ToArray(),
                4 => PUBGData.Scopes.Keys.ToArray(),
                _ => Array.Empty<string>()
            };

            foreach (var item in items) LearnItemCmb.Items.Add(item);
            if (LearnItemCmb.Items.Count > 0) LearnItemCmb.SelectedIndex = 0;
        }

        void SaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (currentReport == null)
            {
                MessageBox.Show(this, "กรุณากดสแกนหน้าจอให้เห็นภาพก่อนบันทึกแม่แบบ", "AI Vision", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int slotIdx = LearnSlotCmb.SelectedIndex + 1;
            var slot = slotIdx == 1 ? currentReport.Slot1 : currentReport.Slot2;
            if (slot == null) return;

            string cat = (LearnCatCmb.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Split(' ')[0] ?? "Weapon";
            string itemName = LearnItemCmb.Text?.Trim();
            if (string.IsNullOrEmpty(itemName))
            {
                MessageBox.Show(this, "กรุณาเลือกหรือระบุชื่อไอเทม", "AI Vision", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Bitmap targetBmp = cat.ToLowerInvariant() switch
            {
                "weapon" => slot.WeaponBmp,
                "scope"  => slot.ScopeBmp,
                "muzzle" => slot.MuzzleBmp,
                "grip"   => slot.GripBmp,
                "stock"  => slot.StockBmp,
                _ => null
            };

            if (targetBmp == null)
            {
                MessageBox.Show(this, $"ไม่พบภาพของส่วน {cat} ในช่อง {slotIdx}", "AI Vision", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TemplateManager.SaveTemplate(cat, itemName, targetBmp);
            MessageBox.Show(this, $"บันทึกแม่แบบ {cat}: {itemName} จากช่อง {slotIdx} สำเร็จแล้ว!\nระบบจะจดจำไอคอนนี้ทันที", "AI Vision", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
