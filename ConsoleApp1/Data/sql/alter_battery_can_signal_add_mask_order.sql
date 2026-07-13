-- BatteryEMU: record mask/shift operation order on battery_can_signal (MariaDB)
--
-- Root-cause fix for decoded values coming back permanently zero on many flag-type signals.
-- "(x & mask) >> shift" and "(x >> shift) & mask" are different operations, but the decoder
-- was applying mask-before-shift unconditionally. Real source code uses both orders — e.g.
-- Tesla's BMS_alertMatrix (CAN ID 0x320) is almost entirely shift-then-mask, while
-- HVP_alertMatrix1 (0x3AA) is almost entirely mask-then-shift. For a single-bit flag (mask
-- 0x01) after a nonzero shift, applying the wrong order doesn't error — it just always
-- evaluates to 0 regardless of the real bit value, which silently reported every such alert
-- flag as permanently inactive.
--
-- Existing imported data does not have this recorded — re-run "Load Current Battery-Emulator
-- Mappings" on the Config page after this migration to get correct values for previously
-- imported batteries.
--
-- Run this once against the batteryemu database (after alter_battery_can_signal_add_decode_fields.sql):
--   mysql -h 192.168.0.224 -u <admin-user> -p batteryemu < alter_battery_can_signal_add_mask_order.sql

ALTER TABLE battery_can_signal
    ADD COLUMN IF NOT EXISTS mask_before_shift BOOLEAN NULL AFTER bit_shift;
