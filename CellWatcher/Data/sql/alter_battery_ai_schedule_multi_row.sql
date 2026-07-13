-- CellWatcher: convert battery_ai_schedule from a single settings row into a
-- multi-row table (one row per scheduled AI report). Only needed if you
-- already ran the original create_battery_ai_schedule.sql (single-row version).
--
-- Run this once against the cellwatcher database:
--   mysql -h 192.168.0.224 -u CellWatcher -p cellwatcher < alter_battery_ai_schedule_multi_row.sql

ALTER TABLE battery_ai_schedule
    MODIFY COLUMN ai_schedule_id INT UNSIGNED NOT NULL AUTO_INCREMENT;

-- The old singleton row defaulted report_type to 'none' (scheduling off) if
-- never configured — that's meaningless in a multi-row list, so drop it.
DELETE FROM battery_ai_schedule WHERE report_type = 'none';
