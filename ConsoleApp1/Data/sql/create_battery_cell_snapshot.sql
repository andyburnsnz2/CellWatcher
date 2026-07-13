-- BatteryEMU: per-cell voltage snapshots (MariaDB)
--
-- One row per saved cell-voltage snapshot (pack-level summary: min/max cell, delta), written by
-- CellHistorianService via MariaDbService.SaveCellSnapshotAsync. Individual cell voltages live in
-- battery_cell_reading, one row per cell per snapshot (see create_battery_cell_reading.sql, which
-- must be run after this one — it has a foreign key back to this table).
--
-- Predates the Data/sql migration convention; reconstructed from the live database's real schema.
--
-- Run this once against the batteryemu database:
--   mysql -h <host> -u BatteryEMU -p batteryemu < create_battery_cell_snapshot.sql

CREATE TABLE IF NOT EXISTS battery_cell_snapshot
(
    cell_snapshot_id BIGINT UNSIGNED   NOT NULL AUTO_INCREMENT,

    read_at          DATETIME(3)       NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

    soc_percent      DECIMAL(6,2)      NULL,
    pack_voltage_v   DECIMAL(9,3)      NULL,

    min_cell_v       DECIMAL(7,4)      NULL,
    max_cell_v       DECIMAL(7,4)      NULL,
    cell_delta_mv    DECIMAL(8,2)      NULL,

    min_cell_no      SMALLINT UNSIGNED NULL,
    max_cell_no      SMALLINT UNSIGNED NULL,
    cell_count       SMALLINT UNSIGNED NOT NULL,

    created_at       DATETIME(3)       NOT NULL DEFAULT CURRENT_TIMESTAMP(3),

    PRIMARY KEY (cell_snapshot_id),
    KEY idx_cell_snapshot_read_at (read_at),
    KEY idx_cell_snapshot_delta (cell_delta_mv),
    KEY idx_cell_snapshot_minmax (min_cell_no, max_cell_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
