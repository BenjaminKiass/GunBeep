-- ============================================================
--  Recoil macro สำหรับ Logitech G Hub  (ใช้คู่กับโปรแกรม GunBeep.exe)
--  GunBeep-Script-Version: 6
--  ความไวเดียวทุกสโคป + ตารางแรงดีดอิงเวลาจริง (ไม่เพี้ยนตาม Sleep)
--
--  เปิด/ปิดมาโคร : ปุ่มลัดที่ตั้งใน GunBeep (ค่าเริ่มต้น F8) หรือคลิกปุ่มใหญ่ในโปรแกรม
--  สถานะส่งมาทางไฟ Scroll Lock + Num Lock:
--    ดับ/ดับ = ปิด    Scroll ติด = ปืน 1    Num ติด = ปืน 2    ติดทั้งคู่ = ปืน 3
--  ปุ่มข้างเมาส์ (Back) ใช้ได้เฉพาะตอนเปิดมาโคร กันค่าเพี้ยนตอนไม่ได้ใช้
--    กดเปล่าๆ = วนปืน (ถ้าปิด UNIFIED_SENS จะวนกล้องแทน)
--    Ctrl+ปุ่ม = base +0.5 / Alt+ปุ่ม = base -0.5
--    Shift+Ctrl+ปุ่ม = x +0.5 / Shift+Alt+ปุ่ม = x -0.5
-- ============================================================

-- ===== ปุ่มและการทำงานหลัก =====
local SCOPE_BUTTON = 5           -- ปุ่มเล็ง (Forward) ถ้าไม่ติดลองเปลี่ยนเป็น 4
local CYCLE_BTN    = 4           -- ปุ่ม Back
local GUN_MOD      = "lshift"    -- ใช้เฉพาะตอน UNIFIED_SENS = false: Shift ค้าง + CYCLE_BTN = วนปืน
local REQUIRE_ADS  = false       -- true = ชดเชยเฉพาะตอนกดปุ่มเล็งค้าง (ไม่ลากตอนยิงสะโพก)

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
  {1,15}, {2,1}, {1,1}, {2,31},
  {3,1}, {2,1}, {3,1}, {2,1}, {3,1}, {2,1}, {3,1}, {2,1}, {3,1},
  {3,243},
}

local GUNS = {
  { name = "gun1", base = 7, x = 0, segs = SEGS_MAIN, sname = "SEGS_MAIN" },
  { name = "gun2", base = 7, x = 0, segs = SEGS_MAIN, sname = "SEGS_MAIN" },
  { name = "gun3", base = 7, x = 0, segs = SEGS_MAIN, sname = "SEGS_MAIN" },
}
local gun_idx = 1

-- ===== ปรับสดระหว่างยิง =====
local X_NUDGE = 1                -- Alt ค้าง = ดึงซ้ายเพิ่ม / Ctrl ค้าง = ดึงขวาเพิ่ม

-- ===== จังหวะเวลา =====
local STEP_MS = 10
local LOOP_MS = 4
local RELEASE_GRACE_MS = 30

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
-- รับครบแล้วตอบกลับด้วย F18
local rx_clk, rx_t, rx_caps, rx_bits, rx_need = false, 0, false, {}, 24

local function rx_take(i, n)
  local v = 0
  for k = i, i + n - 1 do v = v * 2 + rx_bits[k] end
  return v
end

local function rx_apply()
  if rx_take(1, 2) ~= 2 then return end
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
  if n == 9 and rx_need == 9 then rx_need = 9 + rx_take(5, 5) * 12 + 8 end
  if n == rx_need then
    if rx_bits[2] == 1 then rx_apply_segs() else rx_apply() end
  end
end

-- ===== สถานะจาก GunBeep (0 = ปิด, 1-3 = เปิด + เลขปืน) =====
local function read_state()
  local s = IsKeyLockOn("scrolllock")
  local n = IsKeyLockOn("numlock")
  if s and n then return 3 end
  if n then return 2 end
  if s then return 1 end
  return 0
end

-- สลับไฟจากสถานะ from -> to (เปิดดวงที่ต้องติดก่อน จะได้ไม่ผ่านสถานะ "ปิด" ระหว่างทาง)
local function write_state(from, to)
  local fs, fn = (from == 1 or from == 3), (from == 2 or from == 3)
  local ts, tn = (to == 1 or to == 3), (to == 2 or to == 3)
  if ts and not fs then PressAndReleaseKey("scrolllock") end
  if tn and not fn then PressAndReleaseKey("numlock") end
  if fs and not ts then PressAndReleaseKey("scrolllock") end
  if fn and not tn then PressAndReleaseKey("numlock") end
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
    if read_state() == 0 then reason = "macro off" break end
    if REQUIRE_ADS and not IsMouseButtonPressed(SCOPE_BUTTON) then reason = "ads released" break end

    local factor = 1
    if (not UNIFIED_SENS) and IsMouseButtonPressed(SCOPE_BUTTON) then
      factor = scope_factor
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

    -- อ่านสถานะจาก GunBeep
    if GetRunningTime() >= hold_until then
      local st = read_state()
      local now_on = st > 0
      if now_on ~= on then
        on = now_on
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

    -- ปุ่ม Back: ทำงานเฉพาะตอนเปิดมาโคร
    local cyc = IsMouseButtonPressed(CYCLE_BTN)
    if cyc and not prev_cycle and on then
      local sh = IsModifierPressed("lshift")
      local ct = IsModifierPressed("lctrl")
      local al = IsModifierPressed("lalt")
      if sh and (ct or al) then
        tune("x", ct and TUNE_STEP or -TUNE_STEP)
      elseif ct or al then
        tune("base", ct and TUNE_STEP or -TUNE_STEP)
      elseif UNIFIED_SENS or IsModifierPressed(GUN_MOD) then
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
       and ((not REQUIRE_ADS) or IsMouseButtonPressed(SCOPE_BUTTON)) then
      spray()
      while IsMouseButtonPressed(1) do Sleep(5) end
    end

    Sleep(1)
  end
end
