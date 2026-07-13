-- CellWatcher: raw CAN bus capture log (MariaDB)
--
-- Populated by CanFrameUdpListenerService from UDP batches sent by the CanSniffer firmware
-- (LilyGO T-CAN485, see ../../CanSniffer/firmware) — passive listen-only capture of the CAN bus
-- between the battery and the Battery-Emulator. Step one of the CAN-bus project: raw logging
-- only, no analysis yet.
--
-- Run this once against the cellwatcher database:
--   mysql -h 192.168.0.224 -u <admin-user> -p cellwatcher < create_can_frame.sql

CREATE TABLE IF NOT EXISTS can_frame
(
    can_frame_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,

    -- Which Canbus-tab logging session this frame belongs to — see create_can_session.sql.
    -- NULL means captured outside of any session (shouldn't happen going forward, since logging
    -- is only on while a session is active, but kept nullable for that possibility).
    can_session_id BIGINT UNSIGNED NULL,

    -- This server's own wall-clock arrival time — the authoritative timestamp for querying by
    -- time range. device_timestamp_ms below is NOT wall-clock (see that column's comment).
    received_at DATETIME(3) NOT NULL,

    -- The ESP32's own millis() at capture time — device-relative (time since that device booted),
    -- not wall-clock. Useful only for ordering/relative-timing frames within a short window on
    -- the same device session; never compare it across a reboot or against received_at directly.
    device_timestamp_ms INT UNSIGNED NOT NULL,

    can_id INT UNSIGNED NOT NULL,
    is_extended BOOLEAN NOT NULL,
    is_rtr BOOLEAN NOT NULL,
    dlc TINYINT UNSIGNED NOT NULL,
    data VARBINARY(8) NOT NULL,

    PRIMARY KEY (can_frame_id),
    INDEX idx_can_frame_received_at (received_at),
    INDEX idx_can_frame_can_id (can_id),
    INDEX idx_can_frame_session (can_session_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
