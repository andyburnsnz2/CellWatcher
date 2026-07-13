-- BatteryEMU: add hold_at_target_minutes to battery_control_schedule (MariaDB)
--
-- Safe to run even if the column already exists (IF NOT EXISTS). Run this if your
-- battery_control_schedule table predates the "hold at target" feature.
--
--   mysql -h 192.168.0.224 -u <admin-user> -p batteryemu < alter_battery_control_schedule_hold_minutes.sql

ALTER TABLE battery_control_schedule
    ADD COLUMN IF NOT EXISTS hold_at_target_minutes INT NOT NULL DEFAULT 0 AFTER target_soc_percent;
