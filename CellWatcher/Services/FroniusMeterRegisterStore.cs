using CellWatcher.Models;
using FluentModbus;

namespace CellWatcher.Services;

// Builds and updates the Modbus register layouts from fake_meter.py's
// FakeMeterServer._create_fronius_context / _create_sungrow_context and
// _update_modbus_registers.
//
// Unlike the Python original (which ran pymodbus in single=True mode, so it responded no matter
// what unit/slave id a client's request carried), FluentModbus enforces the unit id strictly —
// requests for any id that hasn't been registered via AddUnit get the connection dropped, not
// just ignored. So the configured meter address must be registered as a real unit up front
// (see FroniusMeterService), and every register access here must target that same unit id.
public static class FroniusMeterRegisterStore
{
    // Mirrors FakeMeterServer._default_meter_address.
    public static byte DefaultMeterAddress(string interfaceType)
    {
        if (interfaceType.Contains("Sungrow_dtsu666")) return 254;
        if (interfaceType.Contains("Fronius_ts5ka3")) return 33;
        return 1;
    }

    // These are NOT the literal addresses from the Python source (1, 12, 771, 258, ...) — they're
    // shifted down by one from those. Verified by running the real, unmodified fake_meter.py as a
    // live server and diffing every address against it directly:
    //
    // pymodbus's ModbusSlaveContext applies a +1 translation from wire address to internal dict
    // key on every client read AND on every server-side setValues() call — but NOT on the raw
    // dict literal used to construct the ModbusSparseDataBlock. For the three blocks updated via
    // setValues (258/286/1024 for Fronius, 10/97/119/356 for Sungrow) the write-side +1 and the
    // read-side +1 cancel out, so those addresses are correct exactly as given in the Python
    // source. But for every OTHER register — populated only by the constructor literal, never by
    // setValues — a real client must request address (N-1), not N, to get what the Python source
    // labels "register N". This is why GEN24 was seen requesting address 11 (real firmware value
    // 731, one less than the source's "register 12") and address 35168 (the reserved block the
    // source calls "35169") — those aren't out-of-range probes, they're correct identification
    // reads that this server was answering wrong.
    //
    // A sparse block only serves addresses it was explicitly given — any request touching so much
    // as one address outside this set gets a real Modbus "Illegal Data Address" exception from the
    // Python original, not a zero value. FluentModbus's contiguous buffer has no such concept
    // (everything unwritten is just zero), so this has to be enforced explicitly — see
    // IsAddressRangeValid, used by FroniusMeterService's RequestValidator.
    private static readonly (int Start, int Length)[] FroniusValidRanges =
    [
        (0, 2), (11, 1), (770, 2), (257, 17), (285, 43), (767, 2), (1023, 17),
        (1307, 16), (35168, 16), (4098, 1), (4355, 1), (4356, 2), (20480, 9), (20496, 1),
    ];

    private static readonly (int Start, int Length)[] SungrowValidRanges =
    [
        (10, 12), (63, 1), (97, 6), (119, 1), (356, 16), (20480, 1),
    ];

    public static bool IsAddressRangeValid(string interfaceType, int address, int quantity)
    {
        var ranges = interfaceType.Contains("Sungrow_dtsu666") ? SungrowValidRanges : FroniusValidRanges;
        var end = address + quantity;

        for (var a = address; a < end; a++)
        {
            var covered = false;
            foreach (var (start, length) in ranges)
            {
                if (a >= start && a < start + length)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered) return false;
        }
        return true;
    }

    public static void InitializeStaticRegisters(ModbusTcpServer server, string interfaceType, string fakeUniqueId, byte meterAddress)
    {
        var buffer = server.GetHoldingRegisterBuffer<short>(meterAddress);
        var uniqueIdSuffix = int.TryParse(fakeUniqueId, out var parsed) ? parsed : 1;

        if (interfaceType.Contains("Fronius_ts65a3"))
        {
            WriteFroniusStaticRegisters(
                buffer,
                p0: [2336, 0],
                p20480: [50, 52, 48, 50, 54, 54, 87, 31974, (ushort)(1450 + uniqueIdSuffix)]);
        }
        else if (interfaceType.Contains("Fronius_ts5ka3"))
        {
            WriteFroniusStaticRegisters(
                buffer,
                p0: [2386, 0],
                p20480: [49, 52, 52, 53, 48, 53, 87, 40335, (ushort)(1450 + uniqueIdSuffix)]);
        }
        else if (interfaceType.Contains("Sungrow_dtsu666"))
        {
            WriteSungrowStaticRegisters(buffer);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported Fronius fake-meter interface type: {interfaceType}");
        }

        MirrorHoldingToInputRegisters(server, meterAddress);
    }

    // Encodes the reading into the configured layout and writes it into the server's holding
    // registers. Returns every named block that was written (not just the "main" one used for
    // stuck-value detection, same role as FakeMeterServer.modbus_data in the Python original) so
    // callers — currently just the debug panel — can show exactly what went out over Modbus.
    public static FroniusMeterRegisterWrite WriteDynamicRegisters(ModbusTcpServer server, string interfaceType, FroniusMeterReading reading, byte meterAddress)
    {
        var buffer = server.GetHoldingRegisterBuffer<short>(meterAddress);

        var uMean = (reading.U1 + reading.U2 + reading.U3) / 3.0;
        var uMeanPhases = uMean * 1.73;

        if (interfaceType.Contains("Fronius"))
        {
            var reg258 = FroniusMeterRegisterEncoder.EncodeFroniusMeasurementRegisters(
                uMean, uMeanPhases, reading.PTotal, reading.PaTotal, reading.PrTotal, reading.PfTotal, reading.Frequency);
            var reg286 = FroniusMeterRegisterEncoder.EncodeFroniusPhaseRegisters(
                reading.U1, reading.U2, reading.U3,
                reading.I1, reading.I2, reading.I3,
                reading.P1, reading.P2, reading.P3,
                reading.Pa1, reading.Pa2, reading.Pa3,
                reading.Pr1, reading.Pr2, reading.Pr3,
                reading.Pf1, reading.Pf2, reading.Pf3);
            var reg1024 = FroniusMeterRegisterEncoder.EncodeFroniusEnergyRegisters(
                reading.EConsumed, reading.ErConsumed, reading.EProduced, reading.ErProduced);

            WriteRegisters(buffer, 258, reg258);
            WriteRegisters(buffer, 286, reg286);
            WriteRegisters(buffer, 1024, reg1024);
            MirrorHoldingToInputRegisters(server, meterAddress);

            return new FroniusMeterRegisterWrite("reg_286", reg286, new Dictionary<string, ushort[]>
            {
                ["reg_258"] = reg258,
                ["reg_286"] = reg286,
                ["reg_1024"] = reg1024,
            });
        }

        if (interfaceType.Contains("Sungrow_dtsu666"))
        {
            var reg10 = FroniusMeterRegisterEncoder.EncodeSungrowEnergyRegisters(reading.EConsumed, reading.EProduced);
            var reg97 = FroniusMeterRegisterEncoder.EncodeSungrowVoltageCurrentRegisters(
                reading.U1, reading.U2, reading.U3, reading.I1, reading.I2, reading.I3);
            ushort[] reg119 = [(ushort)Math.Round(reading.Frequency * 100, MidpointRounding.ToEven)];
            var reg356 = FroniusMeterRegisterEncoder.EncodeSungrowActivePowerRegisters(
                reading.P1, reading.P2, reading.P3, reading.PTotal,
                reading.Pr1, reading.Pr2, reading.Pr3, reading.PrTotal);

            WriteRegisters(buffer, 10, reg10);
            WriteRegisters(buffer, 97, reg97);
            WriteRegisters(buffer, 119, reg119);
            WriteRegisters(buffer, 356, reg356);
            MirrorHoldingToInputRegisters(server, meterAddress);

            return new FroniusMeterRegisterWrite("reg_356", reg356, new Dictionary<string, ushort[]>
            {
                ["reg_10"] = reg10,
                ["reg_97"] = reg97,
                ["reg_119"] = reg119,
                ["reg_356"] = reg356,
            });
        }

        throw new InvalidOperationException($"Unsupported Fronius fake-meter interface type: {interfaceType}");
    }

    // Python's original aliased ALL FOUR Modbus data spaces (di/co/hr/ir) to the exact same
    // pymodbus ModbusSparseDataBlock — a request on any of the four function codes returns data
    // derived from identical underlying values. FluentModbus keeps these as four separate
    // buffers, so this replicates that exactly: Input Registers get a straight copy of Holding
    // Registers (both word-addressed, function codes 3/4). Coils and Discrete Inputs are
    // bit-addressed (function codes 1/2) — pymodbus's response encoder packs each raw stored
    // value's truthiness (nonzero -> 1, zero -> 0) as the bit for that address, so that's exactly
    // what's replicated here per address, not a straight byte copy.
    private static void MirrorHoldingToInputRegisters(ModbusTcpServer server, byte meterAddress)
    {
        var holding = server.GetHoldingRegisterBuffer<short>(meterAddress);
        var input = server.GetInputRegisterBuffer<short>(meterAddress);
        holding[..Math.Min(holding.Length, input.Length)].CopyTo(input);

        var coils = server.GetCoilBuffer<byte>(meterAddress);
        var discreteInputs = server.GetDiscreteInputBuffer<byte>(meterAddress);
        var bitCount = Math.Min(holding.Length, Math.Min(coils.Length * 8, discreteInputs.Length * 8));
        for (var i = 0; i < bitCount; i++)
        {
            var bit = holding[i] != 0;
            SetBit(coils, i, bit);
            SetBit(discreteInputs, i, bit);
        }
    }

    private static void SetBit(Span<byte> buffer, int bitIndex, bool value)
    {
        var byteIndex = bitIndex / 8;
        var mask = (byte)(1 << (bitIndex % 8));
        if (value)
            buffer[byteIndex] |= mask;
        else
            buffer[byteIndex] &= (byte)~mask;
    }

    // Every address here is one less than the Python source's own label (see the "-1 shift"
    // explanation on FroniusValidRanges above) — this is deliberate, not a typo.
    private static void WriteFroniusStaticRegisters(Span<short> buffer, ushort[] p0, ushort[] p20480)
    {
        WriteRegisters(buffer, 0, p0);
        WriteRegisters(buffer, 11, [731]);
        WriteRegisters(buffer, 770, [1, 3]);
        WriteRegisters(buffer, 4098, [1]);
        WriteRegisters(buffer, 4355, [1]);
        WriteRegisters(buffer, 4356, [1, 0]);
        WriteRegisters(buffer, 20480, p20480);
        WriteRegisters(buffer, 20496, [2022]);
        // 257, 285, 767, 1023, 1307, 35168 are left at zero until the first MQTT payload
        // arrives, matching the Python ModbusSparseDataBlock's initial zero-filled blocks.
    }

    // Every address here is one less than the Python source's own label. Note this puts the
    // static placeholder at the exact same starting address as the dynamic write below (10, 97,
    // 119, 356) — that's correct: in the Python original the two collide the same way (setValues's
    // own +1 shift lands the dynamic write on top of the static block), so the static value here
    // only ever shows before the very first MQTT update arrives.
    private static void WriteSungrowStaticRegisters(Span<short> buffer)
    {
        WriteRegisters(buffer, 10, [0, 11915, 0, 11915, 0, 0, 0, 0, 0, 0, 0, 3209]);
        WriteRegisters(buffer, 63, [16128]);
        WriteRegisters(buffer, 97, [2300, 2308, 2308, 180, 180, 97]);
        WriteRegisters(buffer, 119, [4999]);
        WriteRegisters(buffer, 356, [0, 269, 65535, 65494, 0, 29, 0, 256, 0, 0, 0, 0, 0, 0, 0, 0]);
        WriteRegisters(buffer, 20480, [8405]);
    }

    // Modbus TCP registers are big-endian on the wire. FluentModbus's Span indexer writes in the
    // host's native (little-endian on x86/x64) byte order and does not swap for you, so a plain
    // indexer assignment here would silently byte-swap every register value on real hardware.
    // SetBigEndian is FluentModbus's helper for storing a value in the correct wire order.
    private static void WriteRegisters(Span<short> buffer, int startAddress, IReadOnlyList<ushort> values)
    {
        for (var i = 0; i < values.Count; i++)
            buffer.SetBigEndian(startAddress + i, unchecked((short)values[i]));
    }
}

// MainBlockName/MainBlock: the block used for stuck-value detection. AllBlocks: every named
// register block written this update, keyed by name — used to render the debug "outgoing" panel.
public sealed record FroniusMeterRegisterWrite(string MainBlockName, ushort[] MainBlock, IReadOnlyDictionary<string, ushort[]> AllBlocks);
