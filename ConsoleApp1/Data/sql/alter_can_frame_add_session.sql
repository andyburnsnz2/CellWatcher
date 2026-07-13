-- BatteryEMU: tag can_frame rows with the logging session they belong to (MariaDB)
--
-- Existing rows (captured before the Canbus tab's Start/Stop sessions existed) are left with
-- can_session_id = NULL — they predate session tracking and won't appear under any session in the
-- UI, but are not deleted.
--
-- Run this once against the batteryemu database (after create_can_session.sql):
--   mysql -h 192.168.0.224 -u <admin-user> -p batteryemu < alter_can_frame_add_session.sql

ALTER TABLE can_frame
    ADD COLUMN IF NOT EXISTS can_session_id BIGINT UNSIGNED NULL AFTER can_frame_id;

CREATE INDEX IF NOT EXISTS idx_can_frame_session ON can_frame (can_session_id);
