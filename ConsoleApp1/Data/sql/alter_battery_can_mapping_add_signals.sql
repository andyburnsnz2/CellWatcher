-- BatteryEMU: replace battery_can_mapping.signal_notes with a proper battery_can_signal child
-- table (MariaDB)
--
-- signal_notes only ever captured DBC-style comments, which turned out to be a rare convention
-- (present in ~1 of ~50 battery files, e.g. Tesla, absent from most others including BMW-I3,
-- ECMP, and everything else checked). Replaced with per-signal extraction of every assignment
-- statement within each CAN ID's case block — this captures the real byte-level decode formula
-- (bit shifts, masks, scale factors against rx_frame.data.u8[N]) that every battery file actually
-- contains, regardless of comment style. See BatteryCanMappingImportService.
--
-- Run this once against the batteryemu database (after create_battery_type.sql):
--   mysql -h 192.168.0.224 -u <admin-user> -p batteryemu < alter_battery_can_mapping_add_signals.sql

ALTER TABLE battery_can_mapping DROP COLUMN IF EXISTS signal_notes;

CREATE TABLE IF NOT EXISTS battery_can_signal
(
    battery_can_signal_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    battery_can_mapping_id BIGINT UNSIGNED NOT NULL,

    -- The assignment target as written in source — either a standardized
    -- DATALAYER_BATTERY_*_TYPE field (e.g. "datalayer_battery->status.voltage_dV", the common
    -- interface every battery driver implements against — see Software/src/datalayer/datalayer.h)
    -- or an intermediate local/member variable (e.g. "battery_volts") that a later statement
    -- (possibly in a different function, e.g. update_values(), not necessarily this same case
    -- block — not something this scraper traces) eventually feeds into that same interface.
    field_name VARCHAR(150) NOT NULL,

    -- The literal right-hand-side source text of the assignment, as written — this is the actual
    -- decode formula (byte offsets, bit masks/shifts, scale factors) when the source computes it
    -- directly from rx_frame.data.u8[N]; truncated at 200 chars for pathologically long
    -- expressions. Source-code scraping, not a guaranteed-correct structured decode — always
    -- cross-check against the real upstream file before relying on it.
    expression_text VARCHAR(210) NULL,

    PRIMARY KEY (battery_can_signal_id),
    INDEX idx_battery_can_signal_mapping (battery_can_mapping_id),
    FOREIGN KEY (battery_can_mapping_id) REFERENCES battery_can_mapping(battery_can_mapping_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
