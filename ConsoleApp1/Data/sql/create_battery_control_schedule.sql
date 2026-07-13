-- BatteryEMU: battery control override schedule (MariaDB)
--
-- Single-row table (always id=1) — one active schedule at a time for the forced-charge /
-- prevent-discharge override that bypasses the normal MQTT-driven Fronius fake-meter feed.
-- Read and applied live (no app restart needed) by BatteryControlService.
--
-- If you already ran an earlier version of this script (with an `enabled` column instead of
-- `activation_mode`), run alter_battery_control_schedule_activation_mode.sql instead of this one.
--
-- Run this once against the batteryemu database:
--   mysql -h 192.168.0.224 -u <admin-user> -p batteryemu < create_battery_control_schedule.sql

CREATE TABLE IF NOT EXISTS battery_control_schedule
(
    battery_control_schedule_id INT UNSIGNED NOT NULL,

    -- 'off' = never runs. 'manual' = only runs when started via the Battery Balancing page's
    -- Start/Stop control (manual_run_requested). 'scheduled' = runs automatically within the
    -- day/time window below.
    activation_mode      VARCHAR(20) NOT NULL DEFAULT 'off',

    -- Set true by the Start button, false by Stop or automatically once target_soc_percent is
    -- reached. Only meaningful when activation_mode = 'manual'.
    manual_run_requested BOOLEAN     NOT NULL DEFAULT FALSE,

    -- 'force_charge' = fake a large grid-export reading so the Fronius inverter's
    -- self-consumption logic charges the battery at its maximum allowed rate.
    -- 'prevent_discharge' = fake a balanced/zero-net reading so the inverter never discharges
    -- the battery to cover apparent load, letting real PV production charge it naturally.
    -- Applies regardless of activation_mode.
    mode               VARCHAR(20) NOT NULL DEFAULT 'force_charge',

    -- Days this schedule is allowed to be active — only used when activation_mode = 'scheduled'.
    monday             BOOLEAN     NOT NULL DEFAULT FALSE,
    tuesday            BOOLEAN     NOT NULL DEFAULT FALSE,
    wednesday          BOOLEAN     NOT NULL DEFAULT FALSE,
    thursday           BOOLEAN     NOT NULL DEFAULT FALSE,
    friday             BOOLEAN     NOT NULL DEFAULT FALSE,
    saturday           BOOLEAN     NOT NULL DEFAULT FALSE,
    sunday             BOOLEAN     NOT NULL DEFAULT FALSE,

    start_time         TIME        NOT NULL DEFAULT '00:00:00',
    end_time           TIME        NOT NULL DEFAULT '23:59:59',

    -- Once real SOC (from BE telemetry via BatteryState) reaches this, the override switches to a
    -- balanced/neutral signal and keeps running for hold_at_target_minutes before actually
    -- stopping — never forces more charge into an already-full pack, but gives cell balancing
    -- circuits time to work while sitting at target. Applies regardless of activation_mode.
    target_soc_percent DECIMAL(5,2) NOT NULL DEFAULT 100.00,
    hold_at_target_minutes INT NOT NULL DEFAULT 0,

    -- Persisted (unlike the rest of this session's "safety reset" thinking) — 500W is only ever
    -- the seed value for a brand-new row here, never re-applied over a value the user has already
    -- set. Restarting the app must never silently discard a deliberately configured charge rate.
    charge_discharge_power_watts INT NOT NULL DEFAULT 500,

    -- Set/cleared by BatteryControlService as the override actually starts/stops — lets the
    -- control page show real activity history, not just the configured schedule.
    last_started_at    DATETIME    NULL,
    last_stopped_at    DATETIME    NULL,

    PRIMARY KEY (battery_control_schedule_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO battery_control_schedule (battery_control_schedule_id) VALUES (1);
