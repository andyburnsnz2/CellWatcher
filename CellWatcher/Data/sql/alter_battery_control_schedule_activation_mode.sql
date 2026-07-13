-- CellWatcher: migrate battery_control_schedule from `enabled` to `activation_mode` (MariaDB)
--
-- Only run this if you already applied the earlier version of create_battery_control_schedule.sql
-- (the one with an `enabled` BOOLEAN column). If you haven't created the table yet, just run the
-- current create_battery_control_schedule.sql instead — it already has activation_mode built in.
--
--   mysql -h 192.168.0.224 -u <admin-user> -p cellwatcher < alter_battery_control_schedule_activation_mode.sql

ALTER TABLE battery_control_schedule
    ADD COLUMN activation_mode      VARCHAR(20) NOT NULL DEFAULT 'off' AFTER battery_control_schedule_id,
    ADD COLUMN manual_run_requested BOOLEAN     NOT NULL DEFAULT FALSE AFTER activation_mode;

UPDATE battery_control_schedule
SET activation_mode = IF(enabled, 'scheduled', 'off');

ALTER TABLE battery_control_schedule
    DROP COLUMN enabled;
