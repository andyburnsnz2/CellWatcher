-- CellWatcher: generic application error log (MariaDB)
--
-- Captures every Error/Critical-level log message written anywhere in the app
-- (via the custom DatabaseErrorLoggerProvider), so failures that only ever
-- scrolled past in the console (MQTT parse errors, AI call failures, DB
-- hiccups, etc.) are visible from the Health page's Errors tab.
--
-- Run this once against the cellwatcher database:
--   mysql -h 192.168.0.224 -u CellWatcher -p cellwatcher < create_application_error.sql

CREATE TABLE IF NOT EXISTS application_error
(
    application_error_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,

    occurred_at     DATETIME     NOT NULL,

    -- Logger category — usually the fully-qualified type that raised it
    -- (e.g. CellWatcher.Services.MqttMessageProcessor)
    source          VARCHAR(200) NOT NULL,

    message         TEXT         NOT NULL,

    exception_type  VARCHAR(200) NULL,
    stack_trace     TEXT         NULL,

    PRIMARY KEY (application_error_id),
    INDEX idx_application_error_occurred_at (occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
