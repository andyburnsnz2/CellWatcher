-- CellWatcher: pack-level readings (MariaDB)
--
-- The foundational table this whole app is built on — one row per saved pack reading
-- (SOC, voltage, current, power, temperature, ...), written by PackHistorianService via
-- MariaDbService.SavePackReadingAsync. Predates the Data/sql migration convention (created
-- ad-hoc early in the project), reconstructed here from the live database's real schema so a
-- fresh install actually has somewhere to write to.
--
-- Run this once against the cellwatcher database:
--   mysql -h <host> -u CellWatcher -p cellwatcher < create_battery_pack_reading.sql

CREATE TABLE IF NOT EXISTS battery_pack_reading
(
    pack_reading_id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,

    read_at                 DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

    soc_percent             DECIMAL(6,2)    NULL,
    soc_real_percent        DECIMAL(6,2)    NULL,
    state_of_health_percent DECIMAL(6,2)    NULL,

    pack_voltage_v          DECIMAL(9,3)    NULL,
    pack_current_a          DECIMAL(9,3)    NULL,
    pack_power_w            DECIMAL(11,2)   NULL,

    temperature_min_c       DECIMAL(6,2)    NULL,
    temperature_max_c       DECIMAL(6,2)    NULL,

    max_discharge_power_w   DECIMAL(11,2)   NULL,
    max_charge_power_w      DECIMAL(11,2)   NULL,

    remaining_capacity_wh   DECIMAL(12,2)   NULL,
    total_capacity_wh       DECIMAL(12,2)   NULL,

    charged_energy_wh       BIGINT UNSIGNED NULL,
    discharged_energy_wh    BIGINT UNSIGNED NULL,

    bms_status              VARCHAR(50)     NULL,
    pause_status            VARCHAR(50)     NULL,
    emulator_status         VARCHAR(50)     NULL,
    event_level             VARCHAR(50)     NULL,

    cpu_temp_c              DECIMAL(6,2)    NULL,
    emulator_uptime_seconds BIGINT UNSIGNED NULL,

    created_at              DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

    PRIMARY KEY (pack_reading_id),
    KEY idx_pack_read_at (read_at),
    KEY idx_pack_soc (soc_percent),
    KEY idx_pack_power (pack_power_w)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
