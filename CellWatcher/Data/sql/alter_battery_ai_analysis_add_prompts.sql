-- CellWatcher: record what was actually sent to the AI alongside its response
--
-- Until now, battery_ai_analysis stored the response but not the request that produced
-- it, so auditing/reproducing a past analysis meant reconstructing the prompt by hand.
-- system_prompt is stored per-row (not just read from current config) because it's
-- user-editable on the Config page — without this, an edited system prompt would make
-- old rows unreproducible. request_prompt is the per-call data digest (the live battery
-- data fed to the model that call). Both MEDIUMTEXT to match response_text's headroom.
--
-- Run once against an existing database:
--   mysql -h 192.168.0.224 -u CellWatcher -p cellwatcher < alter_battery_ai_analysis_add_prompts.sql

ALTER TABLE battery_ai_analysis
    ADD COLUMN system_prompt  MEDIUMTEXT NULL AFTER response_text,
    ADD COLUMN request_prompt MEDIUMTEXT NULL AFTER system_prompt;
