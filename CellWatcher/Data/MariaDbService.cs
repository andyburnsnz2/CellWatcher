using CellWatcher.Models;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace CellWatcher.Data;

public sealed class MariaDbService
{
    private readonly string _connectionString;
    private readonly decimal _cellVoltageChangeThresholdV;
    private readonly int _expectedCellCount;
    private readonly AnalysisThresholds _thresholds;

    public MariaDbService(IConfiguration configuration, AnalysisThresholds thresholds)
    {
        _connectionString = configuration["MariaDb:ConnectionString"]
            ?? throw new InvalidOperationException("Missing MariaDb:ConnectionString");
        _cellVoltageChangeThresholdV = configuration.GetValue<decimal>("CellLogging:CellVoltageChangeMv", 1m) / 1000m;
        _expectedCellCount = configuration.GetValue<int>("CellLogging:ExpectedCellCount", 108);
        _thresholds = thresholds;
    }

    public async Task SavePackReadingAsync(BatterySnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO battery_pack_reading
(
    read_at,
    soc_percent,
    soc_real_percent,
    state_of_health_percent,
    pack_voltage_v,
    pack_current_a,
    pack_power_w,
    temperature_min_c,
    temperature_max_c,
    max_discharge_power_w,
    max_charge_power_w,
    remaining_capacity_wh,
    total_capacity_wh,
    charged_energy_wh,
    discharged_energy_wh,
    bms_status,
    pause_status,
    emulator_status,
    event_level,
    cpu_temp_c,
    emulator_uptime_seconds
)
VALUES
(
    @read_at,
    @soc_percent,
    @soc_real_percent,
    @state_of_health_percent,
    @pack_voltage_v,
    @pack_current_a,
    @pack_power_w,
    @temperature_min_c,
    @temperature_max_c,
    @max_discharge_power_w,
    @max_charge_power_w,
    @remaining_capacity_wh,
    @total_capacity_wh,
    @charged_energy_wh,
    @discharged_energy_wh,
    @bms_status,
    @pause_status,
    @emulator_status,
    @event_level,
    @cpu_temp_c,
    @emulator_uptime_seconds
);";

        command.Parameters.AddWithValue("@read_at", snapshot.ReadAt);
        command.Parameters.AddWithValue("@soc_percent", DbValue(snapshot.SocPercent));
        command.Parameters.AddWithValue("@soc_real_percent", DbValue(snapshot.SocRealPercent));
        command.Parameters.AddWithValue("@state_of_health_percent", DbValue(snapshot.StateOfHealthPercent));
        command.Parameters.AddWithValue("@pack_voltage_v", DbValue(snapshot.PackVoltageV));
        command.Parameters.AddWithValue("@pack_current_a", DbValue(snapshot.PackCurrentA));
        command.Parameters.AddWithValue("@pack_power_w", DbValue(snapshot.PackPowerW));
        command.Parameters.AddWithValue("@temperature_min_c", DbValue(snapshot.TemperatureMinC));
        command.Parameters.AddWithValue("@temperature_max_c", DbValue(snapshot.TemperatureMaxC));
        command.Parameters.AddWithValue("@max_discharge_power_w", DbValue(snapshot.MaxDischargePowerW));
        command.Parameters.AddWithValue("@max_charge_power_w", DbValue(snapshot.MaxChargePowerW));
        command.Parameters.AddWithValue("@remaining_capacity_wh", DbValue(snapshot.RemainingCapacityWh));
        command.Parameters.AddWithValue("@total_capacity_wh", DbValue(snapshot.TotalCapacityWh));
        command.Parameters.AddWithValue("@charged_energy_wh", DbValue(snapshot.ChargedEnergyWh));
        command.Parameters.AddWithValue("@discharged_energy_wh", DbValue(snapshot.DischargedEnergyWh));
        command.Parameters.AddWithValue("@bms_status", DbValue(snapshot.BmsStatus));
        command.Parameters.AddWithValue("@pause_status", DbValue(snapshot.PauseStatus));
        command.Parameters.AddWithValue("@emulator_status", DbValue(snapshot.EmulatorStatus));
        command.Parameters.AddWithValue("@event_level", DbValue(snapshot.EventLevel));
        command.Parameters.AddWithValue("@cpu_temp_c", DbValue(snapshot.CpuTempC));
        command.Parameters.AddWithValue("@emulator_uptime_seconds", DbValue(snapshot.EmulatorUptimeSeconds));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCellSnapshotAsync(BatterySnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var previousCellReadings = await GetPreviousCellReadingsAsync(connection, transaction, cancellationToken);

            await using var snapshotCommand = connection.CreateCommand();
            snapshotCommand.Transaction = transaction;
            snapshotCommand.CommandText = @"
INSERT INTO battery_cell_snapshot
(
    read_at,
    soc_percent,
    pack_voltage_v,
    min_cell_v,
    max_cell_v,
    cell_delta_mv,
    min_cell_no,
    max_cell_no,
    cell_count
)
VALUES
(
    @read_at,
    @soc_percent,
    @pack_voltage_v,
    @min_cell_v,
    @max_cell_v,
    @cell_delta_mv,
    @min_cell_no,
    @max_cell_no,
    @cell_count
);
SELECT LAST_INSERT_ID();";

            snapshotCommand.Parameters.AddWithValue("@read_at", snapshot.ReadAt);
            snapshotCommand.Parameters.AddWithValue("@soc_percent", DbValue(snapshot.SocPercent));
            snapshotCommand.Parameters.AddWithValue("@pack_voltage_v", DbValue(snapshot.PackVoltageV));
            snapshotCommand.Parameters.AddWithValue("@min_cell_v", DbValue(snapshot.MinCellV));
            snapshotCommand.Parameters.AddWithValue("@max_cell_v", DbValue(snapshot.MaxCellV));
            snapshotCommand.Parameters.AddWithValue("@cell_delta_mv", DbValue(snapshot.CellDeltaMv));
            snapshotCommand.Parameters.AddWithValue("@min_cell_no", DbValue(snapshot.MinCellNo));
            snapshotCommand.Parameters.AddWithValue("@max_cell_no", DbValue(snapshot.MaxCellNo));
            snapshotCommand.Parameters.AddWithValue("@cell_count", snapshot.CellVoltages.Count);

            var cellSnapshotId = Convert.ToInt64(
                await snapshotCommand.ExecuteScalarAsync(cancellationToken));

            foreach (var cell in snapshot.CellVoltages.OrderBy(c => c.Key))
            {
                var balancingActive = snapshot.CellBalancing.TryGetValue(cell.Key, out var active) && active;

                if (!ShouldInsertCellReading(previousCellReadings, cell.Key, cell.Value, balancingActive))
                    continue;

                await using var cellCommand = connection.CreateCommand();
                cellCommand.Transaction = transaction;
                cellCommand.CommandText = @"
INSERT INTO battery_cell_reading
(
    cell_snapshot_id,
    cell_no,
    voltage_v,
    balancing_active
)
VALUES
(
    @cell_snapshot_id,
    @cell_no,
    @voltage_v,
    @balancing_active
);";

                cellCommand.Parameters.AddWithValue("@cell_snapshot_id", cellSnapshotId);
                cellCommand.Parameters.AddWithValue("@cell_no", cell.Key);
                cellCommand.Parameters.AddWithValue("@voltage_v", cell.Value);
                cellCommand.Parameters.AddWithValue("@balancing_active", balancingActive);

                await cellCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<Dictionary<int, CellReadingState>> GetPreviousCellReadingsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT r.cell_no, r.voltage_v, r.balancing_active
FROM battery_cell_reading r
INNER JOIN
(
    SELECT cell_no, MAX(cell_snapshot_id) AS cell_snapshot_id
    FROM battery_cell_reading
    GROUP BY cell_no
) latest
    ON latest.cell_no = r.cell_no
    AND latest.cell_snapshot_id = r.cell_snapshot_id;";

        var previousCellReadings = new Dictionary<int, CellReadingState>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var cellNoOrdinal = reader.GetOrdinal("cell_no");
        var voltageOrdinal = reader.GetOrdinal("voltage_v");
        var balancingActiveOrdinal = reader.GetOrdinal("balancing_active");

        while (await reader.ReadAsync(cancellationToken))
        {
            previousCellReadings[reader.GetInt32(cellNoOrdinal)] = new CellReadingState(
                reader.GetDecimal(voltageOrdinal),
                !reader.IsDBNull(balancingActiveOrdinal) && reader.GetBoolean(balancingActiveOrdinal));
        }

        return previousCellReadings;
    }

    private bool ShouldInsertCellReading(
        Dictionary<int, CellReadingState> previousCellReadings,
        int cellNo,
        decimal currentVoltage,
        bool balancingActive)
    {
        if (!previousCellReadings.TryGetValue(cellNo, out var previousReading))
            return true;

        if (previousReading.BalancingActive != balancingActive)
            return true;

        return Math.Abs(currentVoltage - previousReading.VoltageV) >= _cellVoltageChangeThresholdV;
    }

    private static object DbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }


    public async Task<PackCellComparison> GetPackCellComparisonAsync(
    DateTime fromTime,
    DateTime toTime,
    CancellationToken cancellationToken)
    {
        var fromCells = await GetPackCellStateAsync(fromTime, cancellationToken);
        var toCells = await GetPackCellStateAsync(toTime, cancellationToken);

        var cells = Enumerable.Range(1, _expectedCellCount)
            .Select(cellNo => new CellVoltagePoint(
                cellNo,
                fromCells.TryGetValue(cellNo, out var fromVoltage) ? fromVoltage : null,
                toCells.TryGetValue(cellNo, out var toVoltage) ? toVoltage : null))
            .ToList();

        return new PackCellComparison(fromTime, toTime, cells);
    }

    private async Task<Dictionary<int, decimal>> GetPackCellStateAsync(
        DateTime targetTime,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    latest.cell_no,
    r.voltage_v
FROM
(
    SELECT
        r.cell_no,
        MAX(s.cell_snapshot_id) AS latest_cell_snapshot_id
    FROM battery_cell_reading r
    INNER JOIN battery_cell_snapshot s
        ON s.cell_snapshot_id = r.cell_snapshot_id
    WHERE s.read_at <= @target_time
    GROUP BY r.cell_no
) latest
INNER JOIN battery_cell_reading r
    ON r.cell_no = latest.cell_no
   AND r.cell_snapshot_id = latest.latest_cell_snapshot_id
ORDER BY latest.cell_no;";

        command.Parameters.AddWithValue("@target_time", targetTime);

        var result = new Dictionary<int, decimal>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetInt32("cell_no")] = reader.GetDecimal("voltage_v");
        }

        return result;
    }

    // Same "latest reading at or before the target time, per cell" reconstruction as
    // GetPackCellStateAsync above, extended to also carry balancing state — used by the Cells
    // page's time-scrub slider so it can show the full grid (voltage + balancing) as it looked at
    // any point in the selected history window, not just voltage deltas between two points.
    public async Task<CellStateSnapshot> GetCellStateAtAsync(DateTime targetTime, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    latest.cell_no,
    r.voltage_v,
    r.balancing_active
FROM
(
    SELECT
        r.cell_no,
        MAX(s.cell_snapshot_id) AS latest_cell_snapshot_id
    FROM battery_cell_reading r
    INNER JOIN battery_cell_snapshot s
        ON s.cell_snapshot_id = r.cell_snapshot_id
    WHERE s.read_at <= @target_time
    GROUP BY r.cell_no
) latest
INNER JOIN battery_cell_reading r
    ON r.cell_no = latest.cell_no
   AND r.cell_snapshot_id = latest.latest_cell_snapshot_id
ORDER BY latest.cell_no;";

        command.Parameters.AddWithValue("@target_time", targetTime);

        var voltages = new Dictionary<int, decimal>();
        var balancing = new Dictionary<int, bool>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cellNo = reader.GetInt32("cell_no");
            voltages[cellNo] = reader.GetDecimal("voltage_v");
            var balancingOrdinal = reader.GetOrdinal("balancing_active");
            if (!reader.IsDBNull(balancingOrdinal))
                balancing[cellNo] = reader.GetBoolean(balancingOrdinal);
        }

        return new CellStateSnapshot(targetTime, voltages, balancing);
    }

    // Bulk-fetches everything the Cells page's time-scrub slider needs for a whole period in one
    // round-trip: the reconstructed state at fromTime (via GetCellStateAtAsync) plus every change
    // event since, so the client can replay to any point in-memory instead of a query per drag.
    public async Task<CellHistoryEvents> GetCellHistoryEventsAsync(
        DateTime fromTime, DateTime toTime, CancellationToken cancellationToken)
    {
        var baseline = await GetCellStateAtAsync(fromTime, cancellationToken);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = @"
SELECT s.read_at, r.cell_no, r.voltage_v, r.balancing_active
FROM battery_cell_reading r
INNER JOIN battery_cell_snapshot s
    ON s.cell_snapshot_id = r.cell_snapshot_id
WHERE s.read_at > @from_time
  AND s.read_at <= @to_time
ORDER BY s.read_at;";

        command.Parameters.AddWithValue("@from_time", fromTime);
        command.Parameters.AddWithValue("@to_time", toTime);

        var events = new List<CellChangeEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var balancingOrdinal = reader.GetOrdinal("balancing_active");
            events.Add(new CellChangeEvent(
                reader.GetDateTime("read_at"),
                reader.GetInt32("cell_no"),
                reader.GetDecimal("voltage_v"),
                reader.IsDBNull(balancingOrdinal) ? null : reader.GetBoolean(balancingOrdinal)));
        }

        return new CellHistoryEvents(baseline, events);
    }

    public Task<List<PackHealthSample>> GetPackHealthSamplesAsync(
        DateTime fromTime,
        DateTime toTime,
        CancellationToken cancellationToken)
        => GetPackHealthSamplesAsync(fromTime, toTime, maxPoints: null, cancellationToken);

    // maxPoints downsamples for long ranges — each row costs 4 correlated subqueries against
    // battery_pack_reading (nearest-prior current/power/temperature lookups), so a full day at a
    // ~30s snapshot interval was measured taking ~30s to query. Sampling via a modulo filter on
    // cell_snapshot_id is applied in the WHERE clause, before those subqueries run, so skipped
    // rows are genuinely free rather than computed and discarded — a real speedup, not just a
    // smaller result set. Null (the default, used by /api/pack/history's existing "last N hours"
    // callers) preserves the original unsampled behaviour exactly.
    public async Task<List<PackHealthSample>> GetPackHealthSamplesAsync(
        DateTime fromTime,
        DateTime toTime,
        int? maxPoints,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sampleEvery = 1;
        if (maxPoints is > 0)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM battery_cell_snapshot WHERE read_at > @from_time AND read_at <= @to_time;";
            countCommand.Parameters.AddWithValue("@from_time", fromTime);
            countCommand.Parameters.AddWithValue("@to_time", toTime);
            var totalRows = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));
            sampleEvery = (int)Math.Max(1, Math.Ceiling(totalRows / (double)maxPoints.Value));
        }

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = @"
SELECT
    s.read_at,
    s.soc_percent,
    s.pack_voltage_v,
    (
        SELECT p.pack_current_a
        FROM battery_pack_reading p
        WHERE p.read_at <= s.read_at
        ORDER BY p.read_at DESC
        LIMIT 1
    ) AS pack_current_a,
    (
        SELECT p.pack_power_w
        FROM battery_pack_reading p
        WHERE p.read_at <= s.read_at
        ORDER BY p.read_at DESC
        LIMIT 1
    ) AS pack_power_w,
    (
        SELECT p.temperature_min_c
        FROM battery_pack_reading p
        WHERE p.read_at <= s.read_at
        ORDER BY p.read_at DESC
        LIMIT 1
    ) AS temperature_min_c,
    (
        SELECT p.temperature_max_c
        FROM battery_pack_reading p
        WHERE p.read_at <= s.read_at
        ORDER BY p.read_at DESC
        LIMIT 1
    ) AS temperature_max_c,
    s.min_cell_v,
    s.max_cell_v,
    s.cell_delta_mv,
    s.min_cell_no,
    s.max_cell_no
FROM battery_cell_snapshot s
WHERE s.read_at > @from_time
  AND s.read_at <= @to_time
  AND (s.cell_snapshot_id % @sample_every) = 0
ORDER BY s.read_at;";

        command.Parameters.AddWithValue("@from_time", fromTime);
        command.Parameters.AddWithValue("@to_time", toTime);
        command.Parameters.AddWithValue("@sample_every", sampleEvery);

        var samples = new List<PackHealthSample>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new PackHealthSample(
                reader.GetDateTime("read_at"),
                GetNullableDecimal(reader, "soc_percent"),
                GetNullableDecimal(reader, "pack_voltage_v"),
                GetNullableDecimal(reader, "pack_current_a"),
                GetNullableDecimal(reader, "pack_power_w"),
                GetNullableDecimal(reader, "temperature_min_c"),
                GetNullableDecimal(reader, "temperature_max_c"),
                GetNullableDecimal(reader, "min_cell_v"),
                GetNullableDecimal(reader, "max_cell_v"),
                GetNullableDecimal(reader, "cell_delta_mv"),
                GetNullableInt32(reader, "min_cell_no"),
                GetNullableInt32(reader, "max_cell_no")));
        }

        return samples;
    }

    public async Task<List<CellHealthSummary>> GetCellHealthSummariesAsync(
    DateTime fromTime,
    DateTime toTime,
    int windowMinutes,
    CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = @"
WITH RECURSIVE cells(cell_no) AS
(
    SELECT 1
    UNION ALL
    SELECT cell_no + 1
    FROM cells
    WHERE cell_no < @expected_cell_count
),
window_snapshots AS
(
    SELECT
        cell_snapshot_id,
        min_cell_no,
        max_cell_no
    FROM battery_cell_snapshot
    WHERE read_at > @from_time
      AND read_at <= @to_time
),
cell_states AS
(
    SELECT
        s.cell_snapshot_id,
        s.min_cell_no,
        s.max_cell_no,
        c.cell_no,
        (
            SELECT r.voltage_v
            FROM battery_cell_reading r
            WHERE r.cell_no = c.cell_no
              AND r.cell_snapshot_id <= s.cell_snapshot_id
            ORDER BY r.cell_snapshot_id DESC
            LIMIT 1
        ) AS voltage_v
    FROM window_snapshots s
    CROSS JOIN cells c
),
snapshot_averages AS
(
    SELECT
        cell_snapshot_id,
        AVG(voltage_v) AS avg_voltage_v
    FROM cell_states
    WHERE voltage_v IS NOT NULL
    GROUP BY cell_snapshot_id
),
cell_deviations AS
(
    SELECT
        cs.cell_no,
        cs.min_cell_no,
        cs.max_cell_no,
        cs.voltage_v,
        (cs.voltage_v - sa.avg_voltage_v) * 1000 AS deviation_mv
    FROM cell_states cs
    INNER JOIN snapshot_averages sa
        ON sa.cell_snapshot_id = cs.cell_snapshot_id
    WHERE cs.voltage_v IS NOT NULL
)
SELECT
    cell_no,
    COUNT(*) AS reading_count,
    SUM(CASE WHEN min_cell_no = cell_no THEN 1 ELSE 0 END) AS times_min_cell,
    SUM(CASE WHEN max_cell_no = cell_no THEN 1 ELSE 0 END) AS times_max_cell,
    AVG(voltage_v) AS avg_voltage_v,
    AVG(deviation_mv) AS avg_deviation_mv,
    MAX(ABS(deviation_mv)) AS max_deviation_mv
FROM cell_deviations
GROUP BY cell_no
ORDER BY cell_no;";

        command.Parameters.AddWithValue("@from_time", fromTime);
        command.Parameters.AddWithValue("@to_time", toTime);
        command.Parameters.AddWithValue("@expected_cell_count", _expectedCellCount);

        var summaries = new List<CellHealthSummary>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var cellNo = reader.GetInt32("cell_no");
            var readingCount = Convert.ToInt32(reader.GetInt64("reading_count"));
            var timesMinCell = Convert.ToInt32(reader.GetDecimal("times_min_cell"));
            var timesMaxCell = Convert.ToInt32(reader.GetDecimal("times_max_cell"));
            var avgVoltageV = reader.GetDecimal("avg_voltage_v");
            var avgDeviationMv = reader.GetDecimal("avg_deviation_mv");
            var maxDeviationMv = reader.GetDecimal("max_deviation_mv");
            var severity = SeverityFromDeviation(maxDeviationMv);

            summaries.Add(new CellHealthSummary(
                toTime,
                windowMinutes,
                cellNo,
                readingCount,
                timesMinCell,
                timesMaxCell,
                avgVoltageV,
                avgDeviationMv,
                maxDeviationMv,
                severity,
                $"Cell {cellNo}: {readingCount} samples, avg deviation {avgDeviationMv:N2} mV, max deviation {maxDeviationMv:N2} mV"));
        }

        return summaries;
    }

    public async Task<List<CellRankSummary>> GetCellRankSummariesAsync(
        DateTime fromTime,
        DateTime toTime,
        int windowMinutes,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = @"
WITH RECURSIVE cells(cell_no) AS
(
    SELECT 1
    UNION ALL
    SELECT cell_no + 1
    FROM cells
    WHERE cell_no < @expected_cell_count
),
window_snapshots AS
(
    SELECT cell_snapshot_id
    FROM battery_cell_snapshot
    WHERE read_at > @from_time
      AND read_at <= @to_time
),
cell_states AS
(
    SELECT
        s.cell_snapshot_id,
        c.cell_no,
        (
            SELECT r.voltage_v
            FROM battery_cell_reading r
            WHERE r.cell_no = c.cell_no
              AND r.cell_snapshot_id <= s.cell_snapshot_id
            ORDER BY r.cell_snapshot_id DESC
            LIMIT 1
        ) AS voltage_v
    FROM window_snapshots s
    CROSS JOIN cells c
),
ranked AS
(
    SELECT
        cell_snapshot_id,
        cell_no,
        ROW_NUMBER() OVER (PARTITION BY cell_snapshot_id ORDER BY voltage_v ASC) AS low_rank,
        ROW_NUMBER() OVER (PARTITION BY cell_snapshot_id ORDER BY voltage_v DESC) AS high_rank
    FROM cell_states
    WHERE voltage_v IS NOT NULL
)
SELECT
    cell_no,
    COUNT(*) AS reading_count,
    SUM(CASE WHEN low_rank <= 5 THEN 1 ELSE 0 END) AS lowest_five_count,
    SUM(CASE WHEN high_rank <= 5 THEN 1 ELSE 0 END) AS highest_five_count,
    AVG(low_rank) AS avg_rank,
    AVG(high_rank) AS avg_reverse_rank
FROM ranked
GROUP BY cell_no
ORDER BY cell_no;";

        command.Parameters.AddWithValue("@from_time", fromTime);
        command.Parameters.AddWithValue("@to_time", toTime);
        command.Parameters.AddWithValue("@expected_cell_count", _expectedCellCount);

        var summaries = new List<CellRankSummary>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new CellRankSummary(
                toTime,
                windowMinutes,
                reader.GetInt32("cell_no"),
                Convert.ToInt32(reader.GetInt64("reading_count")),
                Convert.ToInt32(reader.GetDecimal("lowest_five_count")),
                Convert.ToInt32(reader.GetDecimal("highest_five_count")),
                reader.GetDecimal("avg_rank"),
                reader.GetDecimal("avg_reverse_rank")));
        }

        return summaries;
    }

    public async Task SaveCellHealthAsync(
    IEnumerable<CellHealthSummary> summaries,
    CancellationToken cancellationToken)
    {
        var summaryList = summaries.ToList();

        if (summaryList.Count == 0)
            return;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var summary in summaryList)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;

                command.CommandText = @"
INSERT INTO battery_cell_health
(
    analysed_at,
    window_minutes,
    cell_no,
    reading_count,
    times_min_cell,
    times_max_cell,
    avg_voltage_v,
    avg_deviation_mv,
    max_deviation_mv,
    severity,
    message
)
VALUES
(
    @analysed_at,
    @window_minutes,
    @cell_no,
    @reading_count,
    @times_min_cell,
    @times_max_cell,
    @avg_voltage_v,
    @avg_deviation_mv,
    @max_deviation_mv,
    @severity,
    @message
);";

                command.Parameters.AddWithValue("@analysed_at", summary.AnalysedAt);
                command.Parameters.AddWithValue("@window_minutes", summary.WindowMinutes);
                command.Parameters.AddWithValue("@cell_no", summary.CellNo);
                command.Parameters.AddWithValue("@reading_count", summary.ReadingCount);
                command.Parameters.AddWithValue("@times_min_cell", summary.TimesMinCell);
                command.Parameters.AddWithValue("@times_max_cell", summary.TimesMaxCell);
                command.Parameters.AddWithValue("@avg_voltage_v", summary.AvgVoltageV);
                command.Parameters.AddWithValue("@avg_deviation_mv", summary.AvgDeviationMv);
                command.Parameters.AddWithValue("@max_deviation_mv", summary.MaxDeviationMv);
                command.Parameters.AddWithValue("@severity", summary.Severity);
                command.Parameters.AddWithValue("@message", summary.Message);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SaveBatteryHealthMetricsAsync(
    IEnumerable<BatteryHealthMetric> metrics,
    CancellationToken cancellationToken)
    {
        var metricList = metrics.ToList();

        if (metricList.Count == 0)
            return;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var metric in metricList)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;

                command.CommandText = @"
INSERT INTO battery_health_metric
(
    analysed_at,
    window_minutes,
    scope,
    cell_no,
    metric_name,
    metric_value,
    metric_value_text,
    metric_unit,
    severity,
    message
)
VALUES
(
    @analysed_at,
    @window_minutes,
    @scope,
    @cell_no,
    @metric_name,
    @metric_value,
    @metric_value_text,
    @metric_unit,
    @severity,
    @message
);";

                command.Parameters.AddWithValue("@analysed_at", metric.AnalysedAt);
                command.Parameters.AddWithValue("@window_minutes", metric.WindowMinutes);
                command.Parameters.AddWithValue("@scope", metric.Scope);
                command.Parameters.AddWithValue("@cell_no", DbValue(metric.CellNo));
                command.Parameters.AddWithValue("@metric_name", metric.MetricName);
                command.Parameters.AddWithValue("@metric_value", DbValue(metric.MetricValue));
                command.Parameters.AddWithValue("@metric_value_text", DbValue(metric.MetricValueText));
                command.Parameters.AddWithValue("@metric_unit", DbValue(metric.MetricUnit));
                command.Parameters.AddWithValue("@severity", metric.Severity);
                command.Parameters.AddWithValue("@message", DbValue(metric.Message));

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<List<BatteryHealthMetric>> GetLatestMetricsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT *
FROM battery_health_metric
WHERE analysed_at = (SELECT MAX(analysed_at) FROM battery_health_metric)
ORDER BY
    CASE severity WHEN 'ALERT' THEN 1 WHEN 'WARN' THEN 2 WHEN 'INFO' THEN 3 ELSE 4 END,
    scope, metric_name, cell_no;";

        var metrics = new List<BatteryHealthMetric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            metrics.Add(new BatteryHealthMetric(
                reader.GetDateTime("analysed_at"),
                reader.GetInt32("window_minutes"),
                reader.GetString("scope"),
                GetNullableInt32(reader, "cell_no"),
                reader.GetString("metric_name"),
                GetNullableDecimal(reader, "metric_value"),
                reader.IsDBNull(reader.GetOrdinal("metric_value_text")) ? null : reader.GetString("metric_value_text"),
                reader.IsDBNull(reader.GetOrdinal("metric_unit")) ? null : reader.GetString("metric_unit"),
                reader.GetString("severity"),
                reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString("message")));
        }

        return metrics;
    }

    public async Task<List<CellHealthSummary>> GetLatestCellHealthAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT *
FROM battery_cell_health
WHERE analysed_at = (SELECT MAX(analysed_at) FROM battery_cell_health)
ORDER BY cell_no, window_minutes;";

        var summaries = new List<CellHealthSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new CellHealthSummary(
                reader.GetDateTime("analysed_at"),
                reader.GetInt32("window_minutes"),
                reader.GetInt32("cell_no"),
                reader.GetInt32("reading_count"),
                reader.GetInt32("times_min_cell"),
                reader.GetInt32("times_max_cell"),
                reader.GetDecimal("avg_voltage_v"),
                reader.GetDecimal("avg_deviation_mv"),
                reader.GetDecimal("max_deviation_mv"),
                reader.GetString("severity"),
                reader.GetString("message")));
        }

        return summaries;
    }

    private string SeverityFromDeviation(decimal deviationMv)
    {
        var abs = Math.Abs(deviationMv);

        return abs >= _thresholds.CellDeviationAlertMv ? "ALERT" :
               abs >= _thresholds.CellDeviationWarnMv  ? "WARN"  :
               abs >= _thresholds.CellDeviationInfoMv  ? "INFO"  :
               "OK";
    }

    private static decimal? GetNullableDecimal(MySqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    private static int? GetNullableInt32(MySqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public async Task SaveAiAnalysisAsync(AiAnalysisRecord record, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO battery_ai_analysis
(
    analysed_at,
    engine,
    engine_model,
    analysis_type,
    period_label,
    period_from,
    period_to,
    success,
    response_text,
    system_prompt,
    request_prompt,
    data_row_count,
    soc_percent_at_analysis,
    pack_voltage_v_at_analysis,
    cell_delta_mv_at_analysis,
    input_tokens,
    output_tokens,
    estimated_cost_usd,
    status_level
)
VALUES
(
    @analysed_at,
    @engine,
    @engine_model,
    @analysis_type,
    @period_label,
    @period_from,
    @period_to,
    @success,
    @response_text,
    @system_prompt,
    @request_prompt,
    @data_row_count,
    @soc_percent_at_analysis,
    @pack_voltage_v_at_analysis,
    @cell_delta_mv_at_analysis,
    @input_tokens,
    @output_tokens,
    @estimated_cost_usd,
    @status_level
);";

        command.Parameters.AddWithValue("@analysed_at", record.AnalysedAt);
        command.Parameters.AddWithValue("@engine", record.Engine);
        command.Parameters.AddWithValue("@engine_model", DbValue(record.EngineModel));
        command.Parameters.AddWithValue("@analysis_type", record.AnalysisType);
        command.Parameters.AddWithValue("@period_label", DbValue(record.PeriodLabel));
        command.Parameters.AddWithValue("@period_from", DbValue(record.PeriodFrom));
        command.Parameters.AddWithValue("@period_to", DbValue(record.PeriodTo));
        command.Parameters.AddWithValue("@success", record.Success);
        command.Parameters.AddWithValue("@response_text", record.ResponseText);
        command.Parameters.AddWithValue("@system_prompt", DbValue(record.SystemPrompt));
        command.Parameters.AddWithValue("@request_prompt", DbValue(record.RequestPrompt));
        command.Parameters.AddWithValue("@data_row_count", DbValue(record.DataRowCount));
        command.Parameters.AddWithValue("@soc_percent_at_analysis", DbValue(record.SocPercentAtAnalysis));
        command.Parameters.AddWithValue("@pack_voltage_v_at_analysis", DbValue(record.PackVoltageVAtAnalysis));
        command.Parameters.AddWithValue("@cell_delta_mv_at_analysis", DbValue(record.CellDeltaMvAtAnalysis));
        command.Parameters.AddWithValue("@input_tokens", DbValue(record.InputTokens));
        command.Parameters.AddWithValue("@output_tokens", DbValue(record.OutputTokens));
        command.Parameters.AddWithValue("@estimated_cost_usd", DbValue(record.EstimatedCostUsd));
        command.Parameters.AddWithValue("@status_level", DbValue(record.StatusLevel));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<AiAnalysisRecord>> GetAiAnalysisHistoryAsync(
        int limit, string? analysisType, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT *
FROM battery_ai_analysis
WHERE (@analysis_type IS NULL OR analysis_type = @analysis_type)
ORDER BY analysed_at DESC
LIMIT @limit;";

        command.Parameters.AddWithValue("@analysis_type", DbValue(analysisType));
        command.Parameters.AddWithValue("@limit", limit);

        var records = new List<AiAnalysisRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
            records.Add(MapAiAnalysisRecord(reader));

        return records;
    }

    public async Task<AiAnalysisRecord?> GetLatestAiAnalysisAsync(
        string engine, string analysisType, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT *
FROM battery_ai_analysis
WHERE engine = @engine
  AND analysis_type = @analysis_type
ORDER BY analysed_at DESC
LIMIT 1;";

        command.Parameters.AddWithValue("@engine", engine);
        command.Parameters.AddWithValue("@analysis_type", analysisType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAiAnalysisRecord(reader) : null;
    }

    // Used to find the last analysis of the same engine/type/period so a new deep
    // analysis can look at only what's new since then instead of the whole period.
    public async Task<AiAnalysisRecord?> GetLatestAiAnalysisByPeriodAsync(
        string engine, string analysisType, string periodLabel, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT *
FROM battery_ai_analysis
WHERE engine = @engine
  AND analysis_type = @analysis_type
  AND period_label = @period_label
  AND success = 1
ORDER BY analysed_at DESC
LIMIT 1;";

        command.Parameters.AddWithValue("@engine", engine);
        command.Parameters.AddWithValue("@analysis_type", analysisType);
        command.Parameters.AddWithValue("@period_label", periodLabel);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAiAnalysisRecord(reader) : null;
    }

    // Cheap COUNT — used to preview how many readings a deep analysis would use
    // without running the full (much more expensive) cell-health/rank queries.
    public async Task<int> GetPackReadingCountAsync(DateTime fromTime, DateTime toTime, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*) FROM battery_cell_snapshot WHERE read_at > @from_time AND read_at <= @to_time;";
        command.Parameters.AddWithValue("@from_time", fromTime);
        command.Parameters.AddWithValue("@to_time", toTime);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    // Tiered trend digest covering the battery's ENTIRE history, deliberately independent of
    // the incremental-window logic used for the analysis period itself. Recent history (last 90
    // days) is bucketed daily; 90 days-2 years back, weekly; beyond 2 years, monthly. This keeps
    // the total line count roughly flat (a few hundred lines) whether the battery has months or
    // decades of history, instead of growing unboundedly if every bucket stayed daily forever —
    // which is what makes it feasible to always include the full lifetime, not just a recent slice.
    public async Task<List<HealthRollupPoint>> GetHealthRollupAsync(DateTime toTime, CancellationToken cancellationToken)
    {
        var dailyFrom = toTime.AddDays(-90);
        var weeklyFrom = toTime.AddDays(-730);
        var monthlyFrom = toTime.AddYears(-50); // effectively "the beginning" — WHERE naturally yields nothing before real data exists

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var points = new List<HealthRollupPoint>();
        points.AddRange(await GetRollupTierAsync(connection, monthlyFrom, weeklyFrom, "month", "DATE_FORMAT(read_at, '%Y-%m-01')", cancellationToken));
        points.AddRange(await GetRollupTierAsync(connection, weeklyFrom, dailyFrom, "week", "DATE_SUB(DATE(read_at), INTERVAL WEEKDAY(read_at) DAY)", cancellationToken));
        points.AddRange(await GetRollupTierAsync(connection, dailyFrom, toTime, "day", "DATE(read_at)", cancellationToken));

        return points.OrderBy(p => p.PeriodStart).ToList();
    }

    // groupByExpr is always one of the three fixed literals passed from GetHealthRollupAsync
    // above, never external/user input, so string-building it into the query is safe here.
    private static async Task<List<HealthRollupPoint>> GetRollupTierAsync(
        MySqlConnection connection, DateTime fromTime, DateTime toTime, string granularity, string groupByExpr, CancellationToken cancellationToken)
    {
        var byBucket = new Dictionary<DateTime, (decimal? AvgDelta, decimal? MinDelta, decimal? MaxDelta, decimal? AvgSoc, decimal? MinSoc, decimal? MaxSoc, decimal? TempMin, decimal? TempMax)>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 120;
            command.CommandText = $@"
SELECT
    {groupByExpr} AS bucket_start,
    AVG(cell_delta_mv) AS avg_delta_mv,
    MIN(cell_delta_mv) AS min_delta_mv,
    MAX(cell_delta_mv) AS max_delta_mv,
    AVG(soc_percent) AS avg_soc_percent,
    MIN(soc_percent) AS min_soc_percent,
    MAX(soc_percent) AS max_soc_percent
FROM battery_cell_snapshot
WHERE read_at > @from_time AND read_at <= @to_time
GROUP BY bucket_start
ORDER BY bucket_start;";
            command.Parameters.AddWithValue("@from_time", fromTime);
            command.Parameters.AddWithValue("@to_time", toTime);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bucket = reader.GetDateTime("bucket_start");
                byBucket[bucket] = (
                    GetNullableDecimal(reader, "avg_delta_mv"),
                    GetNullableDecimal(reader, "min_delta_mv"),
                    GetNullableDecimal(reader, "max_delta_mv"),
                    GetNullableDecimal(reader, "avg_soc_percent"),
                    GetNullableDecimal(reader, "min_soc_percent"),
                    GetNullableDecimal(reader, "max_soc_percent"),
                    null, null);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 120;
            command.CommandText = $@"
SELECT
    {groupByExpr} AS bucket_start,
    MIN(temperature_min_c) AS temp_min_c,
    MAX(temperature_max_c) AS temp_max_c
FROM battery_pack_reading
WHERE read_at > @from_time AND read_at <= @to_time
GROUP BY bucket_start
ORDER BY bucket_start;";
            command.Parameters.AddWithValue("@from_time", fromTime);
            command.Parameters.AddWithValue("@to_time", toTime);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bucket = reader.GetDateTime("bucket_start");
                var tempMin = GetNullableDecimal(reader, "temp_min_c");
                var tempMax = GetNullableDecimal(reader, "temp_max_c");
                byBucket[bucket] = byBucket.TryGetValue(bucket, out var existing)
                    ? existing with { TempMin = tempMin, TempMax = tempMax }
                    : (null, null, null, null, null, null, tempMin, tempMax);
            }
        }

        return byBucket
            .OrderBy(kv => kv.Key)
            .Select(kv => new HealthRollupPoint(
                kv.Key, granularity,
                kv.Value.AvgDelta, kv.Value.MinDelta, kv.Value.MaxDelta,
                kv.Value.AvgSoc, kv.Value.MinSoc, kv.Value.MaxSoc,
                kv.Value.TempMin, kv.Value.TempMax))
            .ToList();
    }

    // Compact one-line-per-report timeline across the AI's ENTIRE analysis history (not just
    // the current analysis period) — LEFT() truncates server-side so this doesn't pull the full
    // (potentially large) response_text/prompt columns over the wire just to build a short gist.
    public async Task<List<AiAnalysisTimelineEntry>> GetAiAnalysisTimelineAsync(
        string analysisType, int limit, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT analysed_at, period_label, status_level, LEFT(response_text, 200) AS response_gist
FROM battery_ai_analysis
WHERE analysis_type = @analysis_type AND success = 1
ORDER BY analysed_at DESC
LIMIT @limit;";
        command.Parameters.AddWithValue("@analysis_type", analysisType);
        command.Parameters.AddWithValue("@limit", limit);

        var entries = new List<AiAnalysisTimelineEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AiAnalysisTimelineEntry(
                reader.GetDateTime("analysed_at"),
                reader.IsDBNull(reader.GetOrdinal("period_label")) ? null : reader.GetString("period_label"),
                reader.IsDBNull(reader.GetOrdinal("status_level")) ? null : reader.GetString("status_level"),
                reader.GetString("response_gist")));
        }

        entries.Reverse(); // oldest first, for a natural chronological read
        return entries;
    }

    // "Greatest-n-per-group": the single most recent successful analysis of each distinct
    // period_label (24h/week/month/...), rather than just the N most recent overall — otherwise
    // frequent daily reports crowd a less-frequent weekly/monthly report out of the AI's context
    // within days of it running, even though it's exactly the perspective a daily report lacks.
    public async Task<List<AiAnalysisRecord>> GetLatestAiAnalysisPerPeriodAsync(string analysisType, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT t.*
FROM battery_ai_analysis t
INNER JOIN
(
    SELECT period_label, MAX(analysed_at) AS max_analysed_at
    FROM battery_ai_analysis
    WHERE analysis_type = @analysis_type AND success = 1
    GROUP BY period_label
) latest
    ON latest.period_label <=> t.period_label
    AND latest.max_analysed_at = t.analysed_at
WHERE t.analysis_type = @analysis_type
ORDER BY t.analysed_at DESC;";

        command.Parameters.AddWithValue("@analysis_type", analysisType);

        var records = new List<AiAnalysisRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(MapAiAnalysisRecord(reader));

        return records;
    }

    private static AiAnalysisRecord MapAiAnalysisRecord(MySqlDataReader reader) => new(
        reader.GetInt64("ai_analysis_id"),
        reader.GetDateTime("analysed_at"),
        reader.GetString("engine"),
        reader.IsDBNull(reader.GetOrdinal("engine_model")) ? null : reader.GetString("engine_model"),
        reader.GetString("analysis_type"),
        reader.IsDBNull(reader.GetOrdinal("period_label")) ? null : reader.GetString("period_label"),
        reader.IsDBNull(reader.GetOrdinal("period_from")) ? null : reader.GetDateTime("period_from"),
        reader.IsDBNull(reader.GetOrdinal("period_to")) ? null : reader.GetDateTime("period_to"),
        reader.GetBoolean("success"),
        reader.GetString("response_text"),
        GetNullableInt32(reader, "data_row_count"),
        GetNullableDecimal(reader, "soc_percent_at_analysis"),
        GetNullableDecimal(reader, "pack_voltage_v_at_analysis"),
        GetNullableDecimal(reader, "cell_delta_mv_at_analysis"),
        GetNullableInt32(reader, "input_tokens"),
        GetNullableInt32(reader, "output_tokens"),
        GetNullableDecimal(reader, "estimated_cost_usd"),
        reader.IsDBNull(reader.GetOrdinal("status_level")) ? null : reader.GetString("status_level"),
        reader.IsDBNull(reader.GetOrdinal("system_prompt")) ? null : reader.GetString("system_prompt"),
        reader.IsDBNull(reader.GetOrdinal("request_prompt")) ? null : reader.GetString("request_prompt"));

    public async Task<List<AiSpendSummary>> GetAiSpendSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    engine,
    COUNT(*) AS analysis_count,
    COALESCE(SUM(input_tokens), 0) AS total_input_tokens,
    COALESCE(SUM(output_tokens), 0) AS total_output_tokens,
    COALESCE(SUM(estimated_cost_usd), 0) AS total_cost_usd
FROM battery_ai_analysis
WHERE success = 1
GROUP BY engine;";

        var summaries = new List<AiSpendSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new AiSpendSummary(
                reader.GetString("engine"),
                Convert.ToInt32(reader.GetInt64("analysis_count")),
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("total_input_tokens"))),
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("total_output_tokens"))),
                reader.GetDecimal("total_cost_usd")));
        }

        return summaries;
    }

    public async Task<List<AiScheduleEntry>> GetAiSchedulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM battery_ai_schedule ORDER BY ai_schedule_id;";

        var entries = new List<AiScheduleEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(MapAiScheduleEntry(reader));

        return entries;
    }

    public async Task<AiScheduleEntry> CreateAiScheduleAsync(AiScheduleEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO battery_ai_schedule
    (run_claude_quick, run_claude_deep, run_chatgpt_quick, run_chatgpt_deep, frequency, time_of_day, day_of_week, day_of_month)
VALUES
    (@run_claude_quick, @run_claude_deep, @run_chatgpt_quick, @run_chatgpt_deep, @frequency, @time_of_day, @day_of_week, @day_of_month);";

        command.Parameters.AddWithValue("@run_claude_quick", entry.RunClaudeQuick);
        command.Parameters.AddWithValue("@run_claude_deep", entry.RunClaudeDeep);
        command.Parameters.AddWithValue("@run_chatgpt_quick", entry.RunChatGptQuick);
        command.Parameters.AddWithValue("@run_chatgpt_deep", entry.RunChatGptDeep);
        command.Parameters.AddWithValue("@frequency", entry.Frequency);
        command.Parameters.AddWithValue("@time_of_day", entry.TimeOfDay);
        command.Parameters.AddWithValue("@day_of_week", DbValue(entry.DayOfWeek));
        command.Parameters.AddWithValue("@day_of_month", DbValue(entry.DayOfMonth));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return entry with { AiScheduleId = command.LastInsertedId };
    }

    public async Task UpdateAiScheduleAsync(long id, AiScheduleEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE battery_ai_schedule SET
    run_claude_quick = @run_claude_quick,
    run_claude_deep = @run_claude_deep,
    run_chatgpt_quick = @run_chatgpt_quick,
    run_chatgpt_deep = @run_chatgpt_deep,
    frequency = @frequency,
    time_of_day = @time_of_day,
    day_of_week = @day_of_week,
    day_of_month = @day_of_month
WHERE ai_schedule_id = @id;";

        command.Parameters.AddWithValue("@run_claude_quick", entry.RunClaudeQuick);
        command.Parameters.AddWithValue("@run_claude_deep", entry.RunClaudeDeep);
        command.Parameters.AddWithValue("@run_chatgpt_quick", entry.RunChatGptQuick);
        command.Parameters.AddWithValue("@run_chatgpt_deep", entry.RunChatGptDeep);
        command.Parameters.AddWithValue("@frequency", entry.Frequency);
        command.Parameters.AddWithValue("@time_of_day", entry.TimeOfDay);
        command.Parameters.AddWithValue("@day_of_week", DbValue(entry.DayOfWeek));
        command.Parameters.AddWithValue("@day_of_month", DbValue(entry.DayOfMonth));
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAiScheduleAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM battery_ai_schedule WHERE ai_schedule_id = @id;";
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAiScheduleLastRunAsync(long id, DateTime lastRunAt, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_ai_schedule SET last_run_at = @last_run_at WHERE ai_schedule_id = @id;";
        command.Parameters.AddWithValue("@last_run_at", lastRunAt);
        command.Parameters.AddWithValue("@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveApplicationErrorAsync(ApplicationErrorRecord record, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO application_error
(
    occurred_at,
    source,
    message,
    exception_type,
    stack_trace
)
VALUES
(
    @occurred_at,
    @source,
    @message,
    @exception_type,
    @stack_trace
);";

        command.Parameters.AddWithValue("@occurred_at", record.OccurredAt);
        command.Parameters.AddWithValue("@source", record.Source);
        command.Parameters.AddWithValue("@message", record.Message);
        command.Parameters.AddWithValue("@exception_type", DbValue(record.ExceptionType));
        command.Parameters.AddWithValue("@stack_trace", DbValue(record.StackTrace));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<ApplicationErrorRecord>> GetApplicationErrorsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT *
FROM application_error
ORDER BY occurred_at DESC
LIMIT @limit;";

        command.Parameters.AddWithValue("@limit", limit);

        var records = new List<ApplicationErrorRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ApplicationErrorRecord(
                reader.GetInt64("application_error_id"),
                reader.GetDateTime("occurred_at"),
                reader.GetString("source"),
                reader.GetString("message"),
                reader.IsDBNull(reader.GetOrdinal("exception_type")) ? null : reader.GetString("exception_type"),
                reader.IsDBNull(reader.GetOrdinal("stack_trace")) ? null : reader.GetString("stack_trace")));
        }

        return records;
    }

    public async Task DeleteAllApplicationErrorsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM application_error;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AiScheduleEntry MapAiScheduleEntry(MySqlDataReader reader)
    {
        return new AiScheduleEntry(
            reader.GetInt64("ai_schedule_id"),
            reader.GetBoolean("run_claude_quick"),
            reader.GetBoolean("run_claude_deep"),
            reader.GetBoolean("run_chatgpt_quick"),
            reader.GetBoolean("run_chatgpt_deep"),
            reader.GetString("frequency"),
            reader.GetTimeSpan("time_of_day"),
            GetNullableInt32(reader, "day_of_week"),
            GetNullableInt32(reader, "day_of_month"),
            reader.IsDBNull(reader.GetOrdinal("last_run_at")) ? null : reader.GetDateTime("last_run_at"));
    }

    // Single-row config (id always 1) — see create_battery_control_schedule.sql, which seeds the
    // one row via INSERT IGNORE, so this SELECT is expected to always find it once that migration
    // has been run.
    public async Task<BatteryControlSchedule> GetBatteryControlScheduleAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM battery_control_schedule WHERE battery_control_schedule_id = 1;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                "battery_control_schedule has no row — run Data/sql/create_battery_control_schedule.sql against the database first.");

        return MapBatteryControlSchedule(reader);
    }

    // Only the user-editable fields — last_started_at/last_stopped_at and manual_run_requested are
    // service-internal state, updated separately (via RecordBatteryControlStartedAsync/StoppedAsync
    // and SetManualRunRequestedAsync) so a schedule save from the Config page never clobbers them.
    public async Task SaveBatteryControlScheduleAsync(BatteryControlSchedule schedule, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE battery_control_schedule SET
    activation_mode = @activation_mode,
    mode = @mode,
    monday = @monday,
    tuesday = @tuesday,
    wednesday = @wednesday,
    thursday = @thursday,
    friday = @friday,
    saturday = @saturday,
    sunday = @sunday,
    start_time = @start_time,
    end_time = @end_time,
    target_soc_percent = @target_soc_percent,
    hold_at_target_minutes = @hold_at_target_minutes
WHERE battery_control_schedule_id = 1;";

        command.Parameters.AddWithValue("@activation_mode", schedule.ActivationMode);
        command.Parameters.AddWithValue("@mode", schedule.Mode);
        command.Parameters.AddWithValue("@monday", schedule.Monday);
        command.Parameters.AddWithValue("@tuesday", schedule.Tuesday);
        command.Parameters.AddWithValue("@wednesday", schedule.Wednesday);
        command.Parameters.AddWithValue("@thursday", schedule.Thursday);
        command.Parameters.AddWithValue("@friday", schedule.Friday);
        command.Parameters.AddWithValue("@saturday", schedule.Saturday);
        command.Parameters.AddWithValue("@sunday", schedule.Sunday);
        command.Parameters.AddWithValue("@start_time", schedule.StartTime);
        command.Parameters.AddWithValue("@end_time", schedule.EndTime);
        command.Parameters.AddWithValue("@target_soc_percent", schedule.TargetSocPercent);
        command.Parameters.AddWithValue("@hold_at_target_minutes", schedule.HoldAtTargetMinutes);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Set true by the Start button on the Battery Balancing page, false by Stop or automatically
    // by BatteryControlService once the target SOC is reached. Only meaningful in "manual" mode.
    public async Task SetManualRunRequestedAsync(bool requested, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_control_schedule SET manual_run_requested = @requested WHERE battery_control_schedule_id = 1;";
        command.Parameters.AddWithValue("@requested", requested);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Persisted — 500W is only ever the seed value for a brand-new row (see
    // create_battery_control_schedule.sql), never re-applied over whatever was last actually set.
    public async Task SetChargeDischargePowerWattsAsync(int watts, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_control_schedule SET charge_discharge_power_watts = @watts WHERE battery_control_schedule_id = 1;";
        command.Parameters.AddWithValue("@watts", watts);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordBatteryControlStartedAsync(DateTime startedAt, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_control_schedule SET last_started_at = @started_at WHERE battery_control_schedule_id = 1;";
        command.Parameters.AddWithValue("@started_at", startedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordBatteryControlStoppedAsync(DateTime stoppedAt, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_control_schedule SET last_stopped_at = @stopped_at WHERE battery_control_schedule_id = 1;";
        command.Parameters.AddWithValue("@stopped_at", stoppedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static BatteryControlSchedule MapBatteryControlSchedule(MySqlDataReader reader)
    {
        return new BatteryControlSchedule(
            reader.GetString("activation_mode"),
            reader.GetBoolean("manual_run_requested"),
            reader.GetString("mode"),
            reader.GetBoolean("monday"),
            reader.GetBoolean("tuesday"),
            reader.GetBoolean("wednesday"),
            reader.GetBoolean("thursday"),
            reader.GetBoolean("friday"),
            reader.GetBoolean("saturday"),
            reader.GetBoolean("sunday"),
            reader.GetTimeSpan("start_time"),
            reader.GetTimeSpan("end_time"),
            reader.GetDecimal("target_soc_percent"),
            reader.GetInt32("hold_at_target_minutes"),
            reader.GetInt32("charge_discharge_power_watts"),
            reader.IsDBNull(reader.GetOrdinal("last_started_at")) ? null : reader.GetDateTime("last_started_at"),
            reader.IsDBNull(reader.GetOrdinal("last_stopped_at")) ? null : reader.GetDateTime("last_stopped_at"));
    }

    // See create_battery_control_history.sql. Purely a log for later analysis — doesn't drive any
    // live behavior. Returns the new row's id so the caller can update it as the run progresses.
    public async Task<int> RecordBatteryControlHistoryStartAsync(
        DateTime startedAt, string activationMode, string mode, decimal targetSocPercent, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO battery_control_history (started_at, activation_mode, mode, target_soc_percent)
VALUES (@started_at, @activation_mode, @mode, @target_soc_percent);
SELECT LAST_INSERT_ID();";
        command.Parameters.AddWithValue("@started_at", startedAt);
        command.Parameters.AddWithValue("@activation_mode", activationMode);
        command.Parameters.AddWithValue("@mode", mode);
        command.Parameters.AddWithValue("@target_soc_percent", targetSocPercent);

        var id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(id);
    }

    public async Task RecordBatteryControlHistoryTargetReachedAsync(int historyId, DateTime reachedAt, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_control_history SET target_reached_at = @reached_at WHERE id = @id;";
        command.Parameters.AddWithValue("@reached_at", reachedAt);
        command.Parameters.AddWithValue("@id", historyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordBatteryControlHistoryStoppedAsync(int historyId, DateTime stoppedAt, string stopReason, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_control_history SET stopped_at = @stopped_at, stop_reason = @stop_reason WHERE id = @id;";
        command.Parameters.AddWithValue("@stopped_at", stoppedAt);
        command.Parameters.AddWithValue("@stop_reason", stopReason);
        command.Parameters.AddWithValue("@id", historyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<BatteryControlHistoryEntry>> GetBatteryControlHistoryAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM battery_control_history ORDER BY started_at DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<BatteryControlHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BatteryControlHistoryEntry(
                reader.GetInt32("id"),
                reader.GetDateTime("started_at"),
                reader.GetString("activation_mode"),
                reader.GetString("mode"),
                reader.GetDecimal("target_soc_percent"),
                reader.IsDBNull(reader.GetOrdinal("target_reached_at")) ? null : reader.GetDateTime("target_reached_at"),
                reader.IsDBNull(reader.GetOrdinal("stopped_at")) ? null : reader.GetDateTime("stopped_at"),
                reader.IsDBNull(reader.GetOrdinal("stop_reason")) ? null : reader.GetString("stop_reason")));
        }
        return results;
    }

    // Rows with stopped_at still NULL — used by BatteryControlService at startup to adopt an
    // in-progress run rather than inserting a duplicate "new" one every time the process
    // restarts while a run is active. Ordered newest-first so the caller can adopt entries[0] and
    // close out anything else here as stale leftovers from previous restarts.
    public async Task<List<BatteryControlHistoryEntry>> GetOpenBatteryControlHistoryEntriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM battery_control_history WHERE stopped_at IS NULL ORDER BY started_at DESC;";

        var results = new List<BatteryControlHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BatteryControlHistoryEntry(
                reader.GetInt32("id"),
                reader.GetDateTime("started_at"),
                reader.GetString("activation_mode"),
                reader.GetString("mode"),
                reader.GetDecimal("target_soc_percent"),
                reader.IsDBNull(reader.GetOrdinal("target_reached_at")) ? null : reader.GetDateTime("target_reached_at"),
                null,
                null));
        }
        return results;
    }

    // See create_can_frame.sql. Chunked into bounded-size multi-row INSERTs (rather than one
    // statement for however many frames the caller hands over) so a single flush of a large
    // backlog can't build one enormous SQL statement against max_allowed_packet.
    private const int CanFrameInsertChunkSize = 500;

    // sessionId: every frame in this batch is tagged with it, and can_session.frame_count is
    // bumped by the total afterward — see CanLoggingSessionState, which gates whether this is
    // ever called with frames at all (nothing is inserted while no session is active).
    public async Task InsertCanFramesAsync(IReadOnlyList<CanFrame> frames, long sessionId, CancellationToken cancellationToken)
    {
        if (frames.Count == 0) return;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        for (var start = 0; start < frames.Count; start += CanFrameInsertChunkSize)
        {
            var count = Math.Min(CanFrameInsertChunkSize, frames.Count - start);
            await InsertCanFrameChunkAsync(connection, frames, start, count, sessionId, cancellationToken);
        }

        await using var updateCountCommand = connection.CreateCommand();
        updateCountCommand.CommandText = "UPDATE can_session SET frame_count = frame_count + @count WHERE can_session_id = @session_id;";
        updateCountCommand.Parameters.AddWithValue("@count", frames.Count);
        updateCountCommand.Parameters.AddWithValue("@session_id", sessionId);
        await updateCountCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCanFrameChunkAsync(
        MySqlConnection connection, IReadOnlyList<CanFrame> frames, int start, int count, long sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;

        var values = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) values.Append(',');
            values.Append($"(@session_id, @received_at{i}, @device_ts{i}, @can_id{i}, @is_extended{i}, @is_rtr{i}, @dlc{i}, @data{i})");
        }

        command.CommandText = $@"
INSERT INTO can_frame (can_session_id, received_at, device_timestamp_ms, can_id, is_extended, is_rtr, dlc, data)
VALUES {values};";

        command.Parameters.AddWithValue("@session_id", sessionId);
        for (var i = 0; i < count; i++)
        {
            var f = frames[start + i];
            command.Parameters.AddWithValue($"@received_at{i}", f.ReceivedAt);
            command.Parameters.AddWithValue($"@device_ts{i}", f.DeviceTimestampMs);
            command.Parameters.AddWithValue($"@can_id{i}", f.CanId);
            command.Parameters.AddWithValue($"@is_extended{i}", f.IsExtended);
            command.Parameters.AddWithValue($"@is_rtr{i}", f.IsRtr);
            command.Parameters.AddWithValue($"@dlc{i}", f.Dlc);
            command.Parameters.AddWithValue($"@data{i}", f.Data);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ── CAN logging sessions (Canbus tab) ──────────────────────────────────────────────────

    public async Task<long> StartCanSessionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO can_session (started_at) VALUES (@started_at); SELECT LAST_INSERT_ID();";
        command.Parameters.AddWithValue("@started_at", DateTime.Now);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id);
    }

    public async Task StopCanSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE can_session SET stopped_at = @stopped_at WHERE can_session_id = @id;";
        command.Parameters.AddWithValue("@stopped_at", DateTime.Now);
        command.Parameters.AddWithValue("@id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<CanSession>> GetCanSessionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM can_session ORDER BY started_at DESC;";

        var results = new List<CanSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CanSession(
                reader.GetInt64("can_session_id"),
                reader.GetDateTime("started_at"),
                reader.IsDBNull(reader.GetOrdinal("stopped_at")) ? null : reader.GetDateTime("stopped_at"),
                reader.GetInt64("frame_count")));
        }
        return results;
    }

    // Used by /api/can-sniffer/status for the Canbus tab's running frame-count stat — deliberately
    // NOT derived from the (capped-at-100) live-view query, which only ever returns at most 100
    // rows regardless of how many frames the session actually has.
    public async Task<CanSession?> GetCanSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM can_session WHERE can_session_id = @id;";
        command.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new CanSession(
            reader.GetInt64("can_session_id"),
            reader.GetDateTime("started_at"),
            reader.IsDBNull(reader.GetOrdinal("stopped_at")) ? null : reader.GetDateTime("stopped_at"),
            reader.GetInt64("frame_count"));
    }

    // Deletes the session's frames first (can_frame has no FK cascade configured), then the
    // session row itself.
    public async Task DeleteCanSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var deleteFramesCommand = connection.CreateCommand();
        deleteFramesCommand.CommandTimeout = 120;
        deleteFramesCommand.CommandText = "DELETE FROM can_frame WHERE can_session_id = @id;";
        deleteFramesCommand.Parameters.AddWithValue("@id", sessionId);
        await deleteFramesCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var deleteSessionCommand = connection.CreateCommand();
        deleteSessionCommand.CommandText = "DELETE FROM can_session WHERE can_session_id = @id;";
        deleteSessionCommand.Parameters.AddWithValue("@id", sessionId);
        await deleteSessionCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    // "Delete everything recorded" — every session and every frame, including any pre-session
    // legacy rows with can_session_id NULL.
    public async Task DeleteAllCanSessionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var deleteFramesCommand = connection.CreateCommand();
        deleteFramesCommand.CommandTimeout = 120;
        deleteFramesCommand.CommandText = "DELETE FROM can_frame;";
        await deleteFramesCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var deleteSessionsCommand = connection.CreateCommand();
        deleteSessionsCommand.CommandText = "DELETE FROM can_session;";
        await deleteSessionsCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    // Newest first, for the Canbus tab's rolling live-view window.
    public async Task<List<CanFrameRecord>> GetRecentCanFramesAsync(long sessionId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM can_frame WHERE can_session_id = @session_id ORDER BY can_frame_id DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@session_id", sessionId);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<CanFrameRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var data = (byte[])reader["data"];
            results.Add(new CanFrameRecord(
                reader.GetInt64("can_frame_id"),
                reader.GetDateTime("received_at"),
                reader.GetUInt32("can_id"),
                reader.GetBoolean("is_extended"),
                reader.GetBoolean("is_rtr"),
                reader.GetByte("dlc"),
                data));
        }
        return results;
    }

    // ── Battery types (imported CAN mappings) ──────────────────────────────────────────────

    public async Task<List<BatteryType>> GetBatteryTypesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM battery_type ORDER BY name;";

        var results = new List<BatteryType>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BatteryType(
                reader.GetInt32("battery_type_id"),
                reader.GetString("name"),
                reader.GetString("source_file"),
                reader.GetString("source_url"),
                reader.GetDateTime("imported_at"),
                reader.GetUInt32("mapping_count")));
        }
        return results;
    }

    // Find-or-create by name, then wholesale-replace its mappings — re-running the import
    // refreshes a battery's mappings cleanly rather than duplicating or leaving stale rows
    // behind if the upstream source changed CAN IDs. Mappings are inserted one at a time (not
    // batched) so each one's generated ID is available immediately to attach its signal rows —
    // batteries have at most ~150 mappings and this only runs on an explicit user-triggered
    // import, so the per-row round trip cost is a non-issue here.
    public async Task ReplaceBatteryTypeMappingsAsync(
        string name, string sourceFile, string sourceUrl,
        IReadOnlyList<ParsedCanMapping> mappings,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var upsertCommand = connection.CreateCommand();
        upsertCommand.CommandText = @"
INSERT INTO battery_type (name, source_file, source_url, imported_at, mapping_count)
VALUES (@name, @source_file, @source_url, @imported_at, @mapping_count)
ON DUPLICATE KEY UPDATE
    source_file = VALUES(source_file),
    source_url = VALUES(source_url),
    imported_at = VALUES(imported_at),
    mapping_count = VALUES(mapping_count);
SELECT battery_type_id FROM battery_type WHERE name = @name;";
        upsertCommand.Parameters.AddWithValue("@name", name);
        upsertCommand.Parameters.AddWithValue("@source_file", sourceFile);
        upsertCommand.Parameters.AddWithValue("@source_url", sourceUrl);
        upsertCommand.Parameters.AddWithValue("@imported_at", DateTime.Now);
        upsertCommand.Parameters.AddWithValue("@mapping_count", mappings.Count);

        // ExecuteScalarAsync on a multi-statement command returns the first result set's first
        // column — the SELECT above, run after the INSERT ... ON DUPLICATE KEY UPDATE.
        var batteryTypeId = Convert.ToInt32(await upsertCommand.ExecuteScalarAsync(cancellationToken));

        // battery_can_signal cascades on delete (FK ON DELETE CASCADE), so this alone clears
        // both tables for this battery.
        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = "DELETE FROM battery_can_mapping WHERE battery_type_id = @id;";
        deleteCommand.Parameters.AddWithValue("@id", batteryTypeId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var mapping in mappings)
        {
            await using var insertMappingCommand = connection.CreateCommand();
            insertMappingCommand.CommandText = @"
INSERT INTO battery_can_mapping (battery_type_id, can_id, frame_name)
VALUES (@battery_type_id, @can_id, @frame_name);
SELECT LAST_INSERT_ID();";
            insertMappingCommand.Parameters.AddWithValue("@battery_type_id", batteryTypeId);
            insertMappingCommand.Parameters.AddWithValue("@can_id", mapping.CanId);
            insertMappingCommand.Parameters.AddWithValue("@frame_name", (object?)mapping.FrameName ?? DBNull.Value);
            var mappingId = Convert.ToInt64(await insertMappingCommand.ExecuteScalarAsync(cancellationToken));

            if (mapping.Signals.Count == 0) continue;

            await using var insertSignalsCommand = connection.CreateCommand();
            var values = new System.Text.StringBuilder();
            for (var i = 0; i < mapping.Signals.Count; i++)
            {
                if (i > 0) values.Append(',');
                values.Append($"(@mapping_id, @field_name{i}, @expr{i}, @bytes{i}, @mask{i}, @shift{i}, @mbs{i}, @scale{i}, @offset{i})");
            }
            insertSignalsCommand.CommandText = $@"
INSERT INTO battery_can_signal
    (battery_can_mapping_id, field_name, expression_text, byte_indices, bit_mask, bit_shift, mask_before_shift, scale, offset_value)
VALUES {values};";
            insertSignalsCommand.Parameters.AddWithValue("@mapping_id", mappingId);
            for (var i = 0; i < mapping.Signals.Count; i++)
            {
                var s = mapping.Signals[i];
                insertSignalsCommand.Parameters.AddWithValue($"@field_name{i}", s.FieldName);
                insertSignalsCommand.Parameters.AddWithValue($"@expr{i}", (object?)s.ExpressionText ?? DBNull.Value);
                insertSignalsCommand.Parameters.AddWithValue($"@bytes{i}", (object?)s.ByteIndices ?? DBNull.Value);
                insertSignalsCommand.Parameters.AddWithValue($"@mask{i}", (object?)s.BitMask ?? DBNull.Value);
                insertSignalsCommand.Parameters.AddWithValue($"@shift{i}", (object?)s.BitShift ?? DBNull.Value);
                insertSignalsCommand.Parameters.AddWithValue($"@mbs{i}", (object?)s.MaskBeforeShift ?? DBNull.Value);
                insertSignalsCommand.Parameters.AddWithValue($"@scale{i}", (object?)s.Scale ?? DBNull.Value);
                insertSignalsCommand.Parameters.AddWithValue($"@offset{i}", (object?)s.OffsetValue ?? DBNull.Value);
            }
            await insertSignalsCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // For displaying a battery's full mapping+signal detail on the Config page.
    public async Task<List<(BatteryCanMapping Mapping, List<BatteryCanSignal> Signals)>> GetBatteryCanMappingsAsync(
        int batteryTypeId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var mappings = new List<BatteryCanMapping>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM battery_can_mapping WHERE battery_type_id = @id ORDER BY can_id;";
            command.Parameters.AddWithValue("@id", batteryTypeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                mappings.Add(new BatteryCanMapping(
                    reader.GetInt64("battery_can_mapping_id"),
                    reader.GetInt32("battery_type_id"),
                    reader.GetUInt32("can_id"),
                    reader.IsDBNull(reader.GetOrdinal("frame_name")) ? null : reader.GetString("frame_name")));
            }
        }

        var signalsByMapping = new Dictionary<long, List<BatteryCanSignal>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
SELECT s.* FROM battery_can_signal s
JOIN battery_can_mapping m ON m.battery_can_mapping_id = s.battery_can_mapping_id
WHERE m.battery_type_id = @id;";
            command.Parameters.AddWithValue("@id", batteryTypeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var mappingId = reader.GetInt64("battery_can_mapping_id");
                var signal = new BatteryCanSignal(
                    reader.GetInt64("battery_can_signal_id"),
                    mappingId,
                    reader.GetString("field_name"),
                    reader.IsDBNull(reader.GetOrdinal("expression_text")) ? null : reader.GetString("expression_text"),
                    reader.IsDBNull(reader.GetOrdinal("byte_indices")) ? null : reader.GetString("byte_indices"),
                    reader.IsDBNull(reader.GetOrdinal("bit_mask")) ? null : reader.GetString("bit_mask"),
                    reader.IsDBNull(reader.GetOrdinal("bit_shift")) ? null : reader.GetInt32("bit_shift"),
                    reader.IsDBNull(reader.GetOrdinal("mask_before_shift")) ? null : reader.GetBoolean("mask_before_shift"),
                    reader.IsDBNull(reader.GetOrdinal("scale")) ? null : reader.GetDouble("scale"),
                    reader.IsDBNull(reader.GetOrdinal("offset_value")) ? null : reader.GetDouble("offset_value"));
                if (!signalsByMapping.TryGetValue(mappingId, out var list))
                    signalsByMapping[mappingId] = list = [];
                list.Add(signal);
            }
        }

        return mappings.Select(m => (m, signalsByMapping.GetValueOrDefault(m.Id, []))).ToList();
    }

    public async Task<int?> GetSelectedBatteryTypeIdAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT selected_battery_type_id FROM battery_selection WHERE battery_selection_id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    public async Task SetSelectedBatteryTypeIdAsync(int? batteryTypeId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE battery_selection SET selected_battery_type_id = @id WHERE battery_selection_id = 1;";
        command.Parameters.AddWithValue("@id", (object?)batteryTypeId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
