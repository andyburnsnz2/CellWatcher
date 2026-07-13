-- CellWatcher: replace battery_ai_schedule.report_type with an explicit
-- per-engine/per-report matrix, and widen frequency to fit 'fortnightly'.
--
-- Run this once against the cellwatcher database:
--   mysql -h 192.168.0.224 -u CellWatcher -p cellwatcher < alter_battery_ai_schedule_engine_matrix.sql

ALTER TABLE battery_ai_schedule
    MODIFY COLUMN frequency VARCHAR(12) NOT NULL DEFAULT 'daily';

ALTER TABLE battery_ai_schedule
    ADD COLUMN run_claude_quick  TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN run_claude_deep   TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN run_chatgpt_quick TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN run_chatgpt_deep  TINYINT(1) NOT NULL DEFAULT 0;

-- Backfill from the old report_type so existing schedules keep running the
-- same as before (AiScheduleService already gates each engine on whether it
-- has an API key configured, so "both engines" here is safe even if only one
-- is actually configured).
UPDATE battery_ai_schedule SET run_claude_quick = 1, run_chatgpt_quick = 1 WHERE report_type IN ('quick', 'both');
UPDATE battery_ai_schedule SET run_claude_deep = 1, run_chatgpt_deep = 1 WHERE report_type IN ('deep', 'both');

ALTER TABLE battery_ai_schedule
    DROP COLUMN report_type;
