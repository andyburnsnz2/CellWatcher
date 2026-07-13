-- BatteryEMU: AI report schedule (MariaDB)
--
-- Multi-row table — each row is one independent scheduled AI report (quick
-- dashboard summary, deep AI Search report, or both) on a daily/weekly/
-- monthly cadence. Read and updated by AiScheduleService. An empty table
-- means no scheduled reports.
--
-- Run this once against the batteryemu database:
--   mysql -h 192.168.0.224 -u BatteryEMU -p batteryemu < create_battery_ai_schedule.sql

CREATE TABLE IF NOT EXISTS battery_ai_schedule
(
    ai_schedule_id  INT UNSIGNED NOT NULL AUTO_INCREMENT,

    -- 'quick' = dashboard summary only, 'deep' = AI Search report only, 'both' = run both
    report_type     VARCHAR(10)  NOT NULL DEFAULT 'both',

    -- 'daily' | 'weekly' | 'monthly' — also determines the period a 'deep' report covers
    -- (Last 24 Hours / Last Week / Last Month respectively)
    frequency       VARCHAR(10)  NOT NULL DEFAULT 'daily',

    time_of_day     TIME         NOT NULL DEFAULT '06:00:00',

    -- 0 = Sunday .. 6 = Saturday; only used when frequency = 'weekly'
    day_of_week     TINYINT UNSIGNED NULL,

    -- 1-31; only used when frequency = 'monthly'. Clamped to the last day of
    -- shorter months (e.g. 31 in February runs on the 28th/29th).
    day_of_month    TINYINT UNSIGNED NULL,

    -- When this row last actually fired — prevents double-firing and lets the
    -- service catch up correctly after downtime.
    last_run_at     DATETIME     NULL,

    PRIMARY KEY (ai_schedule_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
