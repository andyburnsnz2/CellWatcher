-- CellWatcher: AI analysis history table (MariaDB)
--
-- Stores every AI-generated analysis — both the quick dashboard summary and the
-- deep AI Search report — so results can be browsed over time and fed back into
-- future deep analyses as prior-conclusion context.
--
-- Run this once against the cellwatcher database:
--   mysql -h 192.168.0.224 -u CellWatcher -p cellwatcher < create_battery_ai_analysis.sql

CREATE TABLE IF NOT EXISTS battery_ai_analysis
(
    ai_analysis_id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,

    -- When the analysis was generated (server local time, matches read_at/analysed_at elsewhere)
    analysed_at                 DATETIME        NOT NULL,

    -- Which AI produced this result
    engine                      VARCHAR(20)     NOT NULL,   -- 'claude' | 'chatgpt'
    engine_model                VARCHAR(80)     NULL,       -- e.g. 'claude-sonnet-4-6', 'gpt-5'

    -- 'quick' = dashboard summary (rolling ~72h window), 'deep' = AI Search report (explicit period)
    analysis_type                VARCHAR(10)     NOT NULL,

    -- Period the analysis covered. For 'quick' this mirrors the rolling window used;
    -- for 'deep' this is the user-selected period (24h/week/month/year/custom).
    period_label                 VARCHAR(120)    NULL,
    period_from                  DATETIME        NULL,
    period_to                    DATETIME        NULL,

    success                      TINYINT(1)      NOT NULL,
    response_text                MEDIUMTEXT      NOT NULL,

    -- How many pack/cell readings fed into this analysis — lets a reader judge how
    -- much data the conclusion is actually based on (e.g. thin data early on).
    data_row_count                INT UNSIGNED    NULL,

    -- Token usage and estimated cost, computed from the configured per-model $/1M
    -- pricing at the time of the call.
    input_tokens                  INT UNSIGNED    NULL,
    output_tokens                 INT UNSIGNED    NULL,
    estimated_cost_usd            DECIMAL(10,6)   NULL,

    -- Traffic-light summary parsed from the model's mandatory first "STATUS: ..." line —
    -- 'OK' | 'WATCH' | 'ACT', or NULL if the model didn't follow the format.
    status_level                   VARCHAR(10)     NULL,

    -- Lightweight pack snapshot at analysis time, so history is readable on its own
    -- without joining back to battery_pack_reading for context.
    soc_percent_at_analysis      DECIMAL(5,2)    NULL,
    pack_voltage_v_at_analysis   DECIMAL(6,2)    NULL,
    cell_delta_mv_at_analysis    DECIMAL(7,2)    NULL,

    PRIMARY KEY (ai_analysis_id),
    KEY idx_analysed_at (analysed_at),
    KEY idx_engine_type_analysed_at (engine, analysis_type, analysed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
