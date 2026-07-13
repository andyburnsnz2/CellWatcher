-- BatteryEMU: pack/cell health metrics with severity (MariaDB)
--
-- Every individual metric (deviation, delta growth, persistence, risk score, ...) computed each
-- analysis cycle by BatteryHealthAnalysisService via MariaDbService.SaveBatteryHealthMetricsAsync
-- — this is what feeds the Health page's metric table, the dashboard's active alerts, and the AI
-- prompts' "Current Alerts & Warnings" section. Predates the Data/sql migration convention;
-- reconstructed from the live database's real schema.
--
-- Run this once against the batteryemu database:
--   mysql -h <host> -u BatteryEMU -p batteryemu < create_battery_health_metric.sql

CREATE TABLE IF NOT EXISTS battery_health_metric
(
    battery_health_metric_id BIGINT      NOT NULL AUTO_INCREMENT,

    analysed_at              DATETIME(3) NOT NULL,
    window_minutes           INT         NOT NULL,
    scope                    VARCHAR(20) NOT NULL,     -- 'PACK' | 'CELL'
    cell_no                  SMALLINT    NULL,

    metric_name              VARCHAR(100)  NOT NULL,
    metric_value              DECIMAL(18,6) NULL,
    metric_value_text         VARCHAR(255)  NULL,
    metric_unit                VARCHAR(20)  NULL,

    severity                 VARCHAR(20) NOT NULL DEFAULT 'OK',   -- 'OK' | 'INFO' | 'WARN' | 'ALERT'
    message                  VARCHAR(500) NULL,

    PRIMARY KEY (battery_health_metric_id),
    KEY idx_bhm_time (analysed_at),
    KEY idx_bhm_metric_time (metric_name, analysed_at),
    KEY idx_bhm_metric_cell_time (metric_name, cell_no, analysed_at),
    KEY idx_bhm_scope_time (scope, analysed_at),
    KEY idx_bhm_scope_cell_time (scope, cell_no, analysed_at),
    KEY idx_bhm_severity_time (severity, analysed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
