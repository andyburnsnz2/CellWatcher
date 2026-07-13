-- CellWatcher: battery balancing run history (MariaDB)
--
-- One row per balancing run — logged by BatteryControlService as a run starts, reaches its
-- target SOC, and stops. Purely a record for later analysis; doesn't drive any live behavior.
--
-- Run this once against the cellwatcher database:
--   mysql -h 192.168.0.224 -u <admin-user> -p cellwatcher < create_battery_control_history.sql

CREATE TABLE IF NOT EXISTS battery_control_history
(
    id                  INT UNSIGNED NOT NULL AUTO_INCREMENT,

    started_at          DATETIME     NOT NULL,
    activation_mode     VARCHAR(20)  NOT NULL, -- snapshot at run start: 'manual' | 'scheduled'
    mode                VARCHAR(20)  NOT NULL, -- snapshot at run start: 'force_charge' | 'prevent_discharge'
    target_soc_percent  DECIMAL(5,2) NOT NULL, -- snapshot at run start

    -- NULL until the real battery SOC reaches target_soc_percent during this run (NULL forever if
    -- it never did — e.g. the scheduled window closed, or it was stopped manually, first).
    target_reached_at   DATETIME     NULL,

    -- NULL while the run is still active.
    stopped_at          DATETIME     NULL,

    -- 'target_reached_hold_expired' | 'window_closed' | 'manual_stop' | 'app_shutdown'
    stop_reason         VARCHAR(40)  NULL,

    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
