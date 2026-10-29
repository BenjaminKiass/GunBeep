-- ============================================================
--  Recoil macro สำหรับ Logitech G Hub  (ใช้คู่กับโปรแกรม GunBeep.exe)
--  GunBeep-Script-Version: 19
--  ความไวเดียวทุกสโคป + ตารางแรงดีดอิงเวลาจริง (ไม่เพี้ยนตาม Sleep)
--
--  เปิด/ปิดมาโคร : ปุ่มลัดที่ตั้งใน GunBeep (ค่าเริ่มต้น F8) หรือคลิกปุ่มใหญ่ในโปรแกรม
--  เปิด/ปิดส่งมาทางไฟ Scroll Lock (ไม่ใช้ Num Lock แล้ว — ใน PUBG Num Lock = วิ่งอัตโนมัติ)
--  เลขปืนส่งมาเป็นเฟรมสั้นทาง Right Ctrl + Caps Lock; สคริปต์วนปืนเองแล้วบอก GunBeep ด้วย F13/F14/F15
--  ปุ่มข้างเมาส์ (CYCLE_BTN, ค่าเริ่มต้น Back — เปลี่ยนได้ใน GunBeep) ใช้ได้เฉพาะตอนเปิดมาโคร กันค่าเพี้ยนตอนไม่ได้ใช้
--    กดเปล่าๆ = วนปืน (ถ้าปิด UNIFIED_SENS จะวนกล้องแทน)
--    Ctrl+ปุ่ม = base +0.5 / Alt+ปุ่ม = base -0.5
--    Shift+Ctrl+ปุ่ม = x +0.5 / Shift+Alt+ปุ่ม = x -0.5
-- ============================================================

-- ===== ปุ่มและการทำงานหลัก =====
local SCOPE_BUTTON = 3           -- คลิกขวา (ปุ่มเล็ง / ADS)
local CYCLE_BTN    = 4           -- 4 = Back, 5 = Forward, 3 = ปุ่มกลาง, 0 = ไม่ใช้ (GunBeep ตั้งให้)
local GUN_MOD      = "lshift"    -- ใช้เฉพาะตอน UNIFIED_SENS = false: Shift ค้าง + CYCLE_BTN = วนปืน
local REQUIRE_ADS  = false       -- false = ดึงทุกครั้งที่ยิง, true = เฉพาะตอนกดคลิกขวาค้าง (เล็ง ADS) — GunBeep ตั้งให้

-- ===== สัญญาณให้ GunBeep (F21/F22 = base +/-, F19/F20 = x +/-, F23 = เปลี่ยนกล้อง) =====
local SIGNAL_ON = true

-- true  = ในเกมเปิด "ใช้ค่าความไวเดียวกันบนกล้องส่องทั้งหมด" แล้วตั้งทุกค่าเป็น REF_SENS (42)
-- false = ใช้ความไวแยกตามกล้อง ต้องกดวนกล้องให้ตรงกับที่ถือ
local UNIFIED_SENS = true

-- ===== ความไวของกล้อง (ใช้เฉพาะตอน UNIFIED_SENS = false) =====
local REF_SENS = 42
local SCOPE_LIST = { "reddot", "2x", "3x", "4x", "6x", "8x", "15x" }
local SENS = {
  reddot = 36,
  ["2x"] = 42,
  ["3x"] = 44,
  ["4x"] = 53,
  ["6x"] = 53,
  ["8x"] = 44,
  ["15x"] = 45,
}
local scope_idx = 4

-- ===== ปืน (แก้ตารางของแต่ละกระบอกตรงนี้ สูงสุด 3 กระบอก) =====
-- segs  = { {แรงดึง y ต่อ 10ms, จำนวนช่วง}, ... }  (1 ช่วง = STEP_MS)
-- base  = ค่าชดเชยตั้งต้น (เพิ่ม = ดึงลงแรงขึ้น)
-- x     = ซ้าย/ขวา (+ = ขวา, - = ซ้าย)
-- sname = ชื่อตาราง segs (ใช้ตอนพิมพ์ค่าออก Console ให้ก๊อปกลับมาวาง)
local SEGS_MAIN = {
  {1.25,10}, {1.5,10}, {1.75,10}, {2,20}, {2.25,30}, {2.5,30}, {3.75,50}, {4.5,20},
  {4.75,20}, {5.25,30}, {5.5,10}, {5.75,20}, {6,10}, {6.25,20}, {6.5,20}, {6.75,10},
  {7,30},
}

local SEGS_2 = {
  {0.8,8}, {1.4,12}, {2,20}, {2.5,30}, {2.8,40}, {3,50}, {3.2,140},
}

local SEGS_3 = {
  {1,10}, {1.5,20}, {1.75,20}, {3.5,30}, {4,10}, {4.25,30}, {4.75,10}, {5,20},
  {5.5,10}, {5.75,40}, {6,10}, {6.25,10}, {6.5,40}, {6.75,10}, {7,10}, {7.25,10},
  {7.5,10}, {7.75,10}, {8,10}, {8.25,30},
}

local GUNS = {
  { name = "M416", base = 16.5, x = 0, segs = SEGS_MAIN, sname = "SEGS_MAIN" },
  { name = "Beryl", base = 3.5, x = 0, segs = SEGS_2, sname = "SEGS_2" },
  { name = "AUG / ACE", base = 24, x = 0, segs = SEGS_3, sname = "SEGS_3" },
}
local gun_idx = 1

-- ===== ปรับสดระหว่างยิง =====
local X_NUDGE = 1                -- Alt ค้าง = ดึงซ้ายเพิ่ม / Ctrl ค้าง = ดึงขวาเพิ่ม

-- ===== จังหวะเวลา =====
local STEP_MS = 10
local LOOP_MS = 4
local RELEASE_GRACE_MS = 30
local MAX_SHOTS = 42             -- ยิงครบกี่นัดแล้วหยุดดึง (แม็ก 40 + เผื่อ 2) กดยิงค้างตอนกระสุนหมดจอไม่กดลงต่อ — GunBeep ตั้งให้, 0 = ไม่จำกัด
local SHOT_MS = 86               -- เวลาต่อนัดของปืนไรเฟิลส่วนใหญ่ (~700 นัด/นาที)

-- ===== ตัวคูณจาก GunBeep (v19+) =====
-- GEAR_MULT   = ปืน × สโคป × ของแต่ง (GunBeep ตรวจจากหน้ากระเป๋าแล้วคำนวณ ส่งมาตอนปิดกระเป๋า/เปลี่ยนปืน)
-- STANCE_MULT = ยืน / ย่อ / หมอบ (GunBeep ดูปุ่ม C / Z / Space)
local GEAR_MULT = 1
local STANCE_MULT = 1

EnablePrimaryMouseButtonEvents(true)

-- ============================================================
-- เตรียมข้อมูล
-- ============================================================
for _, g in ipairs(GUNS) do
  local t = {}
  for _, s in ipairs(g.segs) do
    for _ = 1, s[2] do t[#t + 1] = s[1] end
  end
  g.rc = t
  g.count = #t
end
local NGUNS = math.min(#GUNS, 3)

local function signal(key)
  if SIGNAL_ON then PressAndReleaseKey(key) end
end

-- ===== รับค่าจาก GunBeep แบบ real time (ไม่ต้องรีสตาร์ท G Hub) =====
-- Right Ctrl เปลี่ยนสถานะ = 1 จังหวะ, Caps Lock สลับในจังหวะนั้น = บิต 1 / ไม่สลับ = บิต 0
-- เฟรม base/x (24 บิต): 10 | ปืน(2) | base*2 (8) | x*2+128 (8) | checksum(4)
-- เฟรมกราฟ: 11 | ปืน(2) | จำนวนก้อน(5) | ก้อนละ: แรงดึง*4 (7) + จำนวนแท่ง 100 ms (5) | checksum(8)
-- เฟรมตั้งค่า (24 บิต): 01 | ชนิด(2) | ค่า(8) | 0(8) | (ชนิด+ค่า)%16 (4)
--   ชนิด 0 = ปุ่มวนปืน (CYCLE_BTN), ชนิด 1 = REQUIRE_ADS (0/1), ชนิด 2 = MAX_SHOTS (0-255)
--   ชนิด 3 = ตัวคูณ: ค่า/64, ช่องที่ 2 = 0 GEAR_MULT, 1 STANCE_MULT — checksum (ชนิด+ค่า+ช่องที่2)%16
-- รับครบแล้วตอบกลับด้วย F18
local rx_clk, rx_t, rx_caps, rx_bits, rx_need = false, 0, false, {}, 24

local function rx_take(i, n)
  local v = 0
  for k = i, i + n - 1 do v = v * 2 + rx_bits[k] end
  return v
end

local function rx_apply_cfg()
  local k, v, sub, chk = rx_take(3, 2), rx_take(5, 8), rx_take(13, 8), rx_take(21, 4)
  if k == 3 then
    if chk ~= (k + v + sub) % 16 or sub > 1 then
      OutputLogMessage("LIVE: ตัวคูณเสีย ไม่รับ\n")
      return
    end
    if sub == 0 then GEAR_MULT = v / 64 else STANCE_MULT = v / 64 end
    OutputLogMessage("LIVE: GEAR=%.3f STANCE=%.3f\n", GEAR_MULT, STANCE_MULT)
    PressAndReleaseKey("f18")
    return
  end
  if chk ~= (k + v) % 16 then
    OutputLogMessage("LIVE: ตั้งค่าเสีย ไม่รับ\n")
    return
  end
  if k == 0 and v <= 5 and v ~= 1 and v ~= 2 then
    CYCLE_BTN = v
    OutputLogMessage("LIVE: ปุ่มวนปืน = %d\n", v)
  elseif k == 1 and v <= 1 then
    REQUIRE_ADS = v == 1
    OutputLogMessage("LIVE: REQUIRE_ADS = %s\n", tostring(REQUIRE_ADS))
  elseif k == 2 then
    MAX_SHOTS = v
    OutputLogMessage("LIVE: MAX_SHOTS = %d\n", v)
  else
    OutputLogMessage("LIVE: ตั้งค่าเสีย ไม่รับ\n")
    return
  end
  PressAndReleaseKey("f18")
end

-- เฟรมปืน (8 บิต): 00 | ปืน(2) | (ปืน*5+3)%16 (4)
local function rx_apply_gun()
  local g, chk = rx_take(3, 2), rx_take(5, 4)
  if g < 1 or g > NGUNS or chk ~= (g * 5 + 3) % 16 then
    OutputLogMessage("LIVE: เลขปืนเสีย ไม่รับ\n")
    return
  end
  gun_idx = g
  OutputLogMessage("LIVE gun=%s base=%g x=%g\n", GUNS[g].name, GUNS[g].base, GUNS[g].x)
  PressAndReleaseKey("f18")
end

local function rx_apply()
  local t = rx_take(1, 2)
  if t == 0 then rx_apply_gun() return end
  if t == 1 then rx_apply_cfg() return end
  if t ~= 2 then return end
  local g, b, xv, chk = rx_take(3, 2), rx_take(5, 8), rx_take(13, 8), rx_take(21, 4)
  if g < 1 or g > NGUNS or chk ~= (g + b + xv) % 16 then
    OutputLogMessage("LIVE: ข้อมูลเสีย ไม่รับ\n")
    return
  end
  GUNS[g].base = b / 2
  GUNS[g].x = (xv - 128) / 2
  OutputLogMessage("LIVE %s: base=%g x=%g\n", GUNS[g].name, GUNS[g].base, GUNS[g].x)
  PressAndReleaseKey("f18")
end

local function rx_apply_segs()
  local g, nr = rx_take(3, 2), rx_take(5, 5)
  local sum, p, runs = g + nr, 10, {}
  for r = 1, nr do
    local v, c = rx_take(p, 7), rx_take(p + 7, 5)
    sum = sum + v + c
    runs[r] = { v / 4, c }
    p = p + 12
  end
  if g < 1 or g > NGUNS or nr < 1 or rx_take(p, 8) ~= sum % 256 then
    OutputLogMessage("LIVE: กราฟเสีย ไม่รับ\n")
    return
  end
  local t = {}
  for _, r in ipairs(runs) do
    for _ = 1, r[2] * 10 do t[#t + 1] = r[1] end
  end
  GUNS[g].rc = t
  GUNS[g].count = #t
  OutputLogMessage("LIVE %s: กราฟใหม่ %d ms\n", GUNS[g].name, #t * STEP_MS)
  PressAndReleaseKey("f18")
end

local function rx_poll()
  local clk = IsModifierPressed("rctrl")
  if clk == rx_clk then return end
  rx_clk = clk
  local now = GetRunningTime()
  local caps = IsKeyLockOn("capslock")
  if now - rx_t > 150 then          -- จังหวะแรกหลังเงียบ = เริ่มเฟรม
    rx_bits = {}
    rx_need = 24
    rx_caps = caps
    rx_t = now
    return
  end
  rx_t = now
  if #rx_bits >= rx_need then return end
  rx_bits[#rx_bits + 1] = (caps ~= rx_caps) and 1 or 0
  rx_caps = caps
  local n = #rx_bits
  if n == 2 and rx_bits[1] == 1 and rx_bits[2] == 1 then rx_need = 9 end     -- เฟรมกราฟ: รอรู้จำนวนก้อนก่อน
  if n == 2 and rx_bits[1] == 0 and rx_bits[2] == 0 then rx_need = 8 end     -- เฟรมปืน: สั้น 8 บิต
  if n == 9 and rx_need == 9 then rx_need = 9 + rx_take(5, 5) * 12 + 8 end
  if n == rx_need then
    if rx_bits[1] == 1 and rx_bits[2] == 1 then rx_apply_segs() else rx_apply() end
  end
end

-- ===== สถานะจาก GunBeep (0 = ปิด, 1-3 = เปิด + เลขปืน) =====
-- ไฟ Scroll Lock = เปิด/ปิด, เลขปืนมาจากเฟรมปืน (gun_idx)
local function read_state()
  if IsKeyLockOn("scrolllock") then return gun_idx end
  return 0
end

-- สคริปต์วนปืนเอง → บอก GunBeep ว่าตอนนี้ปืนไหน
local GUN_KEYS = { "f13", "f14", "f15" }
local function write_state(from, to)
  PressAndReleaseKey(GUN_KEYS[to])
end

-- ===== เล็ง (ADS): นับเฉพาะตอนกดคลิกขวาค้าง (คลิกครั้งเดียวแล้วปล่อย = ไม่นับ) =====
local ads_toggle = false        -- (เลิกใช้แบบกดทีเดียวสโคปค้างแล้ว คงตัวแปรไว้ให้ที่อื่นรีเซ็ตได้)

local function ads_poll() end

local function is_ads()
  return IsMouseButtonPressed(SCOPE_BUTTON)
end

local scope_factor = 1
local function apply_scope()
  if UNIFIED_SENS then
    scope_factor = 1
    OutputLogMessage("scope=ALL (unified sens) factor=1.000\n")
    return
  end
  local name = SCOPE_LIST[scope_idx]
  scope_factor = REF_SENS / (SENS[name] or 53)
  OutputLogMessage("scope=%s factor=%.3f\n", name, scope_factor)
end

local function apply_gun()
  local g = GUNS[gun_idx]
  OutputLogMessage("gun=%s base=%g x=%g\n", g.name, g.base, g.x)
end

-- ===== จูนสด (G Hub เขียนไฟล์ไม่ได้ ค่าจะหายเมื่อโหลดสคริปต์ใหม่) =====
-- ปิดมาโครเมื่อไหร่ Console จะพิมพ์ตาราง GUNS ให้ก๊อปไปวางทับแล้วกด Save
local TUNE_STEP = 0.5

local function gun_line(i)
  local g = GUNS[i]
  return string.format('  { name = "%s", base = %g, x = %g, segs = %s, sname = "%s" },',
                       g.name, g.base, g.x, g.sname, g.sname)
end

local function dump_guns()
  OutputLogMessage("---- ก๊อปไปวางแทนตาราง GUNS ----\n")
  OutputLogMessage("local GUNS = {\n")
  for i = 1, #GUNS do OutputLogMessage("%s\n", gun_line(i)) end
  OutputLogMessage("}\n")
  OutputLogMessage("---------------------------------\n")
end

-- บอก GunBeep ว่าจูนอะไร (มันจะโชว์ค่าและเซฟลงไฟล์สคริปต์ให้)
local TUNE_SIGNAL = { base = { "f21", "f22" }, x = { "f19", "f20" } }

local function tune(field, delta)
  local g = GUNS[gun_idx]
  g[field] = g[field] + delta
  OutputLogMessage("TUNE %s: base=%g x=%g\n", g.name, g.base, g.x)
  signal(delta > 0 and TUNE_SIGNAL[field][1] or TUNE_SIGNAL[field][2])
end

-- ============================================================
-- ยิงหนึ่งชุด: ดึงตามเวลาจริง
-- ============================================================
local function spray()
  local gun = GUNS[gun_idx]
  local rc, n, base = gun.rc, gun.count, gun.base

  local t0 = GetRunningTime()
  local last = 0
  local carryY, carryX = 0, 0
  local release_at = nil
  local reason = "?"

  while true do
    local now = GetRunningTime() - t0

    local pending = false
    if IsMouseButtonPressed(1) then
      release_at = nil
    else
      if not release_at then release_at = now end
      if now - release_at >= RELEASE_GRACE_MS then reason = "mouse released" break end
      pending = true
    end
    rx_poll()                       -- รับค่าใหม่ได้แม้กำลังยิง (ใช้กับชุดถัดไป)
    ads_poll()
    if read_state() == 0 then reason = "macro off" break end
    if MAX_SHOTS > 0 and now >= MAX_SHOTS * SHOT_MS then reason = "max shots" break end
    if REQUIRE_ADS and not is_ads() then reason = "ads released" break end

    local factor = GEAR_MULT * STANCE_MULT
    if (not UNIFIED_SENS) and is_ads() then
      factor = factor * scope_factor
    end

    local dt = now - last
    if dt > 0 and not pending then
      local y = 0
      local k = math.floor(last / STEP_MS) + 1
      while true do
        local s0 = (k - 1) * STEP_MS
        local s1 = k * STEP_MS
        local lo = (last > s0) and last or s0
        local hi = (now < s1) and now or s1
        if hi > lo then
          local idx = (k < n) and k or n
          y = y + (rc[idx] + base) * factor * (hi - lo) / STEP_MS
        end
        if s1 >= now then break end
        k = k + 1
      end

      local bx = gun.x
      if IsModifierPressed("lalt") then bx = bx - X_NUDGE end
      if IsModifierPressed("lctrl") then bx = bx + X_NUDGE end
      local x = bx * dt / STEP_MS

      local wantY = y + carryY
      local moveY = math.floor(wantY + 0.5)
      carryY = wantY - moveY

      local wantX = x + carryX
      local moveX = math.floor(wantX + 0.5)
      carryX = wantX - moveX

      if moveX ~= 0 or moveY ~= 0 then
        MoveMouseRelative(moveX, moveY)
      end
      last = now
    end

    Sleep(LOOP_MS)
  end

  OutputLogMessage("stopped: %s (t=%dms, step=%d)\n",
                   reason, last, math.floor(last / STEP_MS) + 1)
end

-- ============================================================
-- วนหลัก
-- ============================================================
function OnEvent(a, b)
  if a == "PROFILE_ACTIVATED" then
    OutputLogMessage("\nSCRIPT LOADED\n")
    apply_scope()
  elseif a == "PROFILE_DEACTIVATED" then
    ReleaseMouseButton(1)
  end

  local prev_cycle = false
  local on = false
  local hold_until = 0           -- หลังสลับปืนเอง รอไฟอัปเดตก่อนค่อยอ่านกลับ

  local st0 = read_state()
  if st0 > 0 then
    on = true
    gun_idx = math.min(st0, NGUNS)
  end
  OutputLogMessage(on and "MACRO ON\n" or "MACRO OFF\n")
  apply_gun()

  while true do
    rx_poll()
    ads_poll()

    -- อ่านสถานะจาก GunBeep
    if GetRunningTime() >= hold_until then
      local st = read_state()
      local now_on = st > 0
      if now_on ~= on then
        on = now_on
        ads_toggle = false
        if on then
          gun_idx = math.min(st, NGUNS)
          OutputLogMessage("MACRO ON (gun=%s)\n", GUNS[gun_idx].name)
        else
          OutputLogMessage("MACRO OFF\n")
          dump_guns()
        end
      elseif on and math.min(st, NGUNS) ~= gun_idx then
        gun_idx = math.min(st, NGUNS)
        apply_gun()
      end
    end

    -- ปุ่มวนปืน (ค่าเริ่มต้น Back): ทำงานเฉพาะตอนเปิดมาโคร
    -- IsMouseButtonPressed: 2 = ปุ่มกลาง, 3 = คลิกขวา → CYCLE_BTN 3 (ปุ่มกลาง) ต้องอ่านเป็น 2
    local cyc = CYCLE_BTN > 0 and IsMouseButtonPressed(CYCLE_BTN == 3 and 2 or CYCLE_BTN)
    if cyc and not prev_cycle and on then
      local sh = IsModifierPressed("lshift")
      local ct = IsModifierPressed("lctrl")
      local al = IsModifierPressed("lalt")
      if sh and (ct or al) then
        tune("x", ct and TUNE_STEP or -TUNE_STEP)
      elseif ct or al then
        tune("base", ct and TUNE_STEP or -TUNE_STEP)
      elseif UNIFIED_SENS or IsModifierPressed(GUN_MOD) then
        ads_toggle = false
        local nxt = gun_idx % NGUNS + 1
        write_state(gun_idx, nxt)
        gun_idx = nxt
        hold_until = GetRunningTime() + 300
        apply_gun()
      else
        scope_idx = scope_idx % #SCOPE_LIST + 1
        apply_scope()
        signal("f23")
      end
    end
    prev_cycle = cyc

    if on and IsMouseButtonPressed(1)
       and ((not REQUIRE_ADS) or is_ads()) then
      spray()
      while IsMouseButtonPressed(1) do rx_poll(); Sleep(5) end   -- ยังรับค่าจาก GunBeep ระหว่างกดค้างหลังหยุดดึง
    end

    Sleep(1)
  end
end
