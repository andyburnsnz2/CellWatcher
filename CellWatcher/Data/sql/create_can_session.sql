-- CellWatcher: CAN bus logging sessions (MariaDB)
--
-- A "session" groups the can_frame rows captured between a Start and a Stop press on the Canbus
-- tab. Logging is off by default — CanFrameUdpListenerService only writes incoming frames to
-- can_frame while a session is active (see stopped_at IS NULL = currently running), tagging them
-- with can_session_id (see alter_can_frame_add_session.sql).
--
-- Run this once against the cellwatcher database:
--   mysql -h 192.168.0.224 -u <admin-user> -p cellwatcher < create_can_session.sql

CREATE TABLE IF NOT EXISTS can_session
(
    can_session_id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    started_at      DATETIME(3) NOT NULL,
    stopped_at      DATETIME(3) NULL, -- NULL = still running (at most one such row at a time)

    -- Denormalized running count — avoids a COUNT(*) over can_frame (which can be large) every
    -- time the sessions list is displayed. Bumped by CanFrameUdpListenerService on each batch
    -- insert; only ever informational, not relied on for correctness.
    frame_count     BIGINT UNSIGNED NOT NULL DEFAULT 0,

    PRIMARY KEY (can_session_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
