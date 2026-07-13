-- BatteryEMU: individual cell voltage readings (MariaDB)
--
-- One row per cell per snapshot (only written when that cell's voltage or balancing state
-- actually changed beyond the configured threshold — see MariaDbService.ShouldInsertCellReading).
-- Foreign-keyed to battery_cell_snapshot, so create_battery_cell_snapshot.sql must be run first.
--
-- Predates the Data/sql migration convention; reconstructed from the live database's real schema.
--
-- Run this once against the batteryemu database, after create_battery_cell_snapshot.sql:
--   mysql -h <host> -u BatteryEMU -p batteryemu < create_battery_cell_reading.sql

CREATE TABLE IF NOT EXISTS battery_cell_reading
(
    cell_reading_id   BIGINT UNSIGNED   NOT NULL AUTO_INCREMENT,
    cell_snapshot_id  BIGINT UNSIGNED   NOT NULL,

    cell_no           SMALLINT UNSIGNED NOT NULL,
    voltage_v         DECIMAL(7,4)      NOT NULL,
    balancing_active  TINYINT(1)        NULL,

    PRIMARY KEY (cell_reading_id),
    UNIQUE KEY uq_snapshot_cell (cell_snapshot_id, cell_no),
    KEY idx_cell_no (cell_no),
    KEY idx_cell_reading_cell_snap (cell_no, cell_snapshot_id),
    CONSTRAINT fk_cell_snapshot FOREIGN KEY (cell_snapshot_id)
        REFERENCES battery_cell_snapshot (cell_snapshot_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
