namespace CellWatcher.Services;

// Direct port of the register encoding in fake_meter.py (FakeMeterServer._encode_* /
// _convert_values_to_register). Register layouts, scaling factors, and word ordering are
// copied byte-for-byte from the Python implementation that has been running against the real
// Fronius/Sungrow hardware for years — do not "clean up" the asymmetries (e.g. low-word-first
// for Fronius vs high-word-first for Sungrow) without re-verifying against real hardware.
public static class FroniusMeterRegisterEncoder
{
    // High word first. Used for Sungrow 32-bit values.
    public static ushort[] EncodeSigned32Bit(int value)
    {
        ushort low, high;

        if (value >= 0)
        {
            low = (ushort)(value & 0xFFFF);
            high = (ushort)((value >> 16) & 0xFFFF);
        }
        else
        {
            var unsigned = (uint)(-value);
            low = (ushort)(0xFFFF - (unsigned & 0xFFFF));
            high = (ushort)(0xFFFF - ((unsigned >> 16) & 0xFFFF));
        }

        return [high, low];
    }

    // Low word first. Used for Fronius 32-bit values.
    public static ushort[] EncodeSigned32BitLowHigh(int value)
    {
        var encoded = EncodeSigned32Bit(value);
        return [encoded[1], encoded[0]];
    }

    public static ushort[] EncodeFroniusEnergyRegisters(double eConsumed, double erConsumed, double eProduced, double erProduced)
    {
        List<ushort> result = [];
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate(eConsumed)));
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate((eConsumed - Math.Truncate(eConsumed)) * 1000)));
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate(erConsumed)));
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate((erConsumed - Math.Truncate(erConsumed)) * 1000)));
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate(eProduced)));
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate((eProduced - Math.Truncate(eProduced)) * 1000)));
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate(erProduced)));
        result.AddRange(EncodeSigned32BitLowHigh((int)Math.Truncate((erProduced - Math.Truncate(erProduced)) * 1000)));
        return [.. result];
    }

    public static ushort[] EncodeFroniusMeasurementRegisters(
        double uMean, double uMeanPhases, double pTotal, double paTotal, double prTotal, double pfTotal, double frequency)
    {
        List<ushort> result = [];
        result.AddRange(EncodeSigned32BitLowHigh(Round(uMean * 10)));
        result.AddRange(EncodeSigned32BitLowHigh(Round(uMeanPhases * 10)));
        result.AddRange(EncodeSigned32BitLowHigh(Round(pTotal * 10)));
        result.AddRange(EncodeSigned32BitLowHigh(Round(paTotal * 10)));
        result.AddRange(EncodeSigned32BitLowHigh(Round(prTotal * 10)));
        result.AddRange(EncodeSigned32BitLowHigh(Round(pfTotal * 1000)));
        result.AddRange(EncodeSigned32BitLowHigh(0));
        result.AddRange(EncodeSigned32BitLowHigh(Round(frequency * 10)));
        return [.. result];
    }

    public static ushort[] EncodeFroniusPhaseRegisters(
        double u1, double u2, double u3,
        double i1, double i2, double i3,
        double p1, double p2, double p3,
        double pa1, double pa2, double pa3,
        double pr1, double pr2, double pr3,
        double pf1, double pf2, double pf3)
    {
        List<ushort> result = [];
        AddPhase(result, u1, i1, p1, pa1, pr1, pf1);
        AddPhase(result, u2, i2, p2, pa2, pr2, pf2);
        AddPhase(result, u3, i3, p3, pa3, pr3, pf3);
        return [.. result];

        static void AddPhase(List<ushort> target, double u, double i, double p, double pa, double pr, double pf)
        {
            target.AddRange(EncodeSigned32BitLowHigh(Round(u * 1.73 * 10)));
            target.AddRange(EncodeSigned32BitLowHigh(Round(u * 10)));
            target.AddRange(EncodeSigned32BitLowHigh(Round(Math.Abs(i) * 1000)));
            target.AddRange(EncodeSigned32BitLowHigh(Round(p * 10)));
            target.AddRange(EncodeSigned32BitLowHigh(Round(pa * 10)));
            target.AddRange(EncodeSigned32BitLowHigh(Round(pr * 10)));
            target.AddRange(EncodeSigned32BitLowHigh(Round(pf * 1000)));
        }
    }

    public static ushort[] EncodeSungrowActivePowerRegisters(
        double p1, double p2, double p3, double pTotal,
        double pr1, double pr2, double pr3, double prTotal)
    {
        List<ushort> result = [];
        result.AddRange(EncodeSigned32Bit(Round(p1)));
        result.AddRange(EncodeSigned32Bit(Round(p2)));
        result.AddRange(EncodeSigned32Bit(Round(p3)));
        result.AddRange(EncodeSigned32Bit(Round(pTotal)));
        result.AddRange(EncodeSigned32Bit(Round(pr1)));
        result.AddRange(EncodeSigned32Bit(Round(pr2)));
        result.AddRange(EncodeSigned32Bit(Round(pr3)));
        result.AddRange(EncodeSigned32Bit(Round(prTotal)));
        return [.. result];
    }

    public static ushort[] EncodeSungrowVoltageCurrentRegisters(
        double u1, double u2, double u3, double i1, double i2, double i3) =>
        [
            (ushort)Round(u1 * 10),
            (ushort)Round(u2 * 10),
            (ushort)Round(u3 * 10),
            (ushort)Round(Math.Abs(i1) * 100),
            (ushort)Round(Math.Abs(i2) * 100),
            (ushort)Round(Math.Abs(i3) * 100),
        ];

    public static ushort[] EncodeSungrowEnergyRegisters(double eConsumed, double eProduced)
    {
        var consumed = EncodeSigned32Bit(Round(eConsumed * 100));
        var produced = EncodeSigned32Bit(Round(eProduced * 100));

        return
        [
            consumed[0], consumed[1],
            consumed[0], consumed[1],
            0, 0, 0, 0, 0, 0,
            produced[0], produced[1],
        ];
    }

    // Matches Python's round(): round-half-to-even, not away-from-zero.
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.ToEven);
}
