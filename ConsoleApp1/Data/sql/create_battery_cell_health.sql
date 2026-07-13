-- BatteryEMU: per-cell health summaries (MariaDB)
--
-- Rolling-window (1h/24h) per-cell deviation summaries, written by BatteryHealthAnalysisService
-- via MariaDbService.SaveCellHealthAsync. Predates the Data/sql migration convention; reconstructed
-- from the live database's real schema.
--
-- Run this once against the batteryemu database:
--   mysql -h <host> -u BatteryEMU -p batteryemu < create_battery_cell_health.sql

CREATE TABLE IF NOT EXISTS battery_cell_health
(
    battery_cell_health_id BIGINT       NOT NULL AUTO_INCREMENT,

    analysed_at             DATETIME(3) NOT NULL,
    window_minutes           INT        NOT NULL,
    cell_no                  INT        NOT NULL,

    reading_count            INT        NOT NULL,
    times_min_cell           INT        NOT NULL,
    times_max_cell           INT        NOT NULL,

    avg_voltage_v            DECIMAL(8,5)  NULL,
    avg_deviation_mv         DECIMAL(10,3) NULL,
    max_deviation_mv         DECIMAL(10,3) NULL,

    severity                 VARCHAR(20)   NOT NULL,
    message                  TEXT          NULL,

    PRIMARY KEY (battery_cell_health_id),
    KEY idx_cell_health_time (analysed_at),
    KEY idx_cell_health_cell_time (cell_no, analysed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
