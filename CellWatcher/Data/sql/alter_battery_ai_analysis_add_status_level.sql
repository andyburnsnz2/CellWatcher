-- CellWatcher: add the traffic-light status column to battery_ai_analysis
--
-- Run once against an existing database:
--   mysql -h 192.168.0.224 -u CellWatcher -p cellwatcher < alter_battery_ai_analysis_add_status_level.sql

ALTER TABLE battery_ai_analysis
    ADD COLUMN status_level VARCHAR(10) NULL AFTER estimated_cost_usd;
