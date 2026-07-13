-- BatteryEMU: add token-usage / estimated-cost tracking to battery_ai_analysis
--
-- Run once against an existing database that already has battery_ai_analysis:
--   mysql -h 192.168.0.224 -u BatteryEMU -p batteryemu < alter_battery_ai_analysis_add_cost.sql

ALTER TABLE battery_ai_analysis
    ADD COLUMN input_tokens       INT UNSIGNED  NULL AFTER data_row_count,
    ADD COLUMN output_tokens      INT UNSIGNED  NULL AFTER input_tokens,
    ADD COLUMN estimated_cost_usd DECIMAL(10,6) NULL AFTER output_tokens;
