-- CellWatcher: add charge_discharge_power_watts to battery_control_schedule (MariaDB)
--
-- Safe to run even if the column already exists (IF NOT EXISTS). Run this if your
-- battery_control_schedule table predates persisting this value — previously it was held
-- in-memory only and reset to 500W on every app restart, which turned out to silently discard a
-- deliberately configured charge rate every time the app was redeployed/restarted.
--
--   mysql -h 192.168.0.224 -u <admin-user> -p cellwatcher < alter_battery_control_schedule_power_watts.sql

ALTER TABLE battery_control_schedule
    ADD COLUMN IF NOT EXISTS charge_discharge_power_watts INT NOT NULL DEFAULT 500 AFTER hold_at_target_minutes;
