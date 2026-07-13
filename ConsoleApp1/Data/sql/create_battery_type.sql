-- BatteryEMU: battery types + their CAN ID mappings, imported from the Battery-Emulator
-- source repository (MariaDB)
--
-- Populated by BatteryCanMappingImportService, which trawls
-- github.com/dalathegreat/Battery-Emulator's Software/src/battery directory — one row per
-- concrete battery implementation file (identified by actually containing a
-- "switch (rx_frame.ID)" receive dispatcher, not by filename guessing, so shared/base-class
-- files like Battery.cpp or CanBattery.cpp are correctly skipped).
--
-- Every battery in this repo communicates over CAN, not Modbus, despite how this feature is
-- often described informally — battery_can_mapping.can_id is the frame's CAN identifier.
--
-- Run this once against the batteryemu database:
--   mysql -h 192.168.0.224 -u <admin-user> -p batteryemu < create_battery_type.sql

CREATE TABLE IF NOT EXISTS battery_type
(
    battery_type_id INT UNSIGNED NOT NULL AUTO_INCREMENT,

    -- Derived from the source filename, e.g. "TESLA-BATTERY" from TESLA-BATTERY.cpp — this is
    -- what the dropdown on the Config page shows and what re-imports match on to replace
    -- (rather than duplicate) an existing battery's mappings.
    name VARCHAR(100) NOT NULL,

    source_file VARCHAR(200) NOT NULL,
    source_url  VARCHAR(500) NOT NULL,
    imported_at DATETIME NOT NULL,
    mapping_count INT UNSIGNED NOT NULL DEFAULT 0, -- denormalized, avoids a COUNT(*) for the dropdown

    UNIQUE KEY uq_battery_type_name (name),
    PRIMARY KEY (battery_type_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS battery_can_mapping
(
    battery_can_mapping_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    battery_type_id INT UNSIGNED NOT NULL,

    can_id INT UNSIGNED NOT NULL,

    -- The trailing comment on the "case 0x...:" line, e.g. "850 BMS_energyStatus newer BMS" —
    -- almost always a human-written frame name/description, not guaranteed present or
    -- consistently formatted (source code comments, not a real schema).
    frame_name VARCHAR(255) NULL,

    PRIMARY KEY (battery_can_mapping_id),
    INDEX idx_battery_can_mapping_battery (battery_type_id),
    INDEX idx_battery_can_mapping_can_id (can_id),
    FOREIGN KEY (battery_type_id) REFERENCES battery_type(battery_type_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- See alter_battery_can_mapping_add_signals.sql's header comment for why this is a proper child
-- table rather than a single text blob column.
CREATE TABLE IF NOT EXISTS battery_can_signal
(
    battery_can_signal_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    battery_can_mapping_id BIGINT UNSIGNED NOT NULL,
    field_name VARCHAR(150) NOT NULL,
    expression_text VARCHAR(210) NULL,

    -- Structured decode fields — see alter_battery_can_signal_add_decode_fields.sql for what
    -- these mean and their limits. Best-effort parse of expression_text; null when the
    -- expression doesn't fit the pattern they're extracted from.
    byte_indices VARCHAR(50) NULL,
    bit_mask VARCHAR(20) NULL,
    bit_shift TINYINT NULL,

    -- Whether the source applies "& mask" before ">> shift" or after — see
    -- alter_battery_can_signal_add_mask_order.sql. Not cosmetic: applying the wrong order for a
    -- single-bit flag after a nonzero shift always evaluates to 0 regardless of the real value.
    mask_before_shift BOOLEAN NULL,

    scale DOUBLE NULL,
    offset_value DOUBLE NULL,

    PRIMARY KEY (battery_can_signal_id),
    INDEX idx_battery_can_signal_mapping (battery_can_mapping_id),
    FOREIGN KEY (battery_can_mapping_id) REFERENCES battery_can_mapping(battery_can_mapping_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Single-row setting: which imported battery type this deployment is currently configured for.
-- Not consumed by any decoding logic yet — this is groundwork for that, not the decoder itself.
CREATE TABLE IF NOT EXISTS battery_selection
(
    battery_selection_id INT UNSIGNED NOT NULL,
    selected_battery_type_id INT UNSIGNED NULL,
    PRIMARY KEY (battery_selection_id),
    FOREIGN KEY (selected_battery_type_id) REFERENCES battery_type(battery_type_id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO battery_selection (battery_selection_id) VALUES (1);
