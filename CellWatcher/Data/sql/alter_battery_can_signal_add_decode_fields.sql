-- CellWatcher: structured decode fields on battery_can_signal (MariaDB)
--
-- Parses the raw expression_text (see alter_battery_can_mapping_add_signals.sql) into actual
-- columns wherever the expression matches common byte-decode patterns
-- (rx_frame.data.u8[N] combined via <<, |, &, >>, optionally scaled/offset by a trailing
-- * factor or +/- constant). Not every expression fits this shape (boolean comparisons, calls to
-- other functions, references to other local variables) — those keep expression_text as the only
-- record, with the new columns left NULL. This is still source-code scraping of hand-written C++,
-- not a guaranteed-correct structured decode.
--
-- Run this once against the cellwatcher database (after alter_battery_can_mapping_add_signals.sql):
--   mysql -h 192.168.0.224 -u <admin-user> -p cellwatcher < alter_battery_can_signal_add_decode_fields.sql

ALTER TABLE battery_can_signal
    -- Comma-separated byte indices referenced, in the order written (typically high-byte first
    -- for multi-byte fields, e.g. "1,0" for (u8[1]<<8)|u8[0]) — NOT necessarily true byte/bit
    -- endianness metadata, just which rx_frame.data.u8[N] indices appear in the expression.
    ADD COLUMN IF NOT EXISTS byte_indices VARCHAR(50) NULL AFTER expression_text,

    -- Hex mask if a "& 0x.." / "& (0x..U)" was found anywhere in the expression, e.g. "0x07".
    ADD COLUMN IF NOT EXISTS bit_mask VARCHAR(20) NULL AFTER byte_indices,

    -- Shift amount if a ">> N" was found (relative shift within the expression, not necessarily
    -- a single well-defined "the" shift if the expression combines several).
    ADD COLUMN IF NOT EXISTS bit_shift TINYINT NULL AFTER bit_mask,

    -- Multiplier if a trailing "* <number>" was found, e.g. 0.1 in "(...) * 0.1".
    ADD COLUMN IF NOT EXISTS scale DOUBLE NULL AFTER bit_shift,

    -- Additive constant if a trailing "+ <number>" / "- <number>" was found at the outer level,
    -- e.g. -819.2 in "(...) - 819.2".
    ADD COLUMN IF NOT EXISTS offset_value DOUBLE NULL AFTER scale;
