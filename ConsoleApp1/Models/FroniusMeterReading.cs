using System.Text.Json.Serialization;

namespace BatteryEMU.Models;

// Mirrors the JSON payload published by the real_meter.py / Home Assistant side of the
// Fronius fake-meter setup (see Fronius Meter/Python Version/fake_meter.py). Field names are
// the short codes used on the wire: e=energy, p=power, u=voltage, i=current, f=frequency;
// a=apparent, r=reactive, f(second letter)=factor; a trailing digit is the phase, "t" is total.
//
// last_updated is published as a quoted string (e.g. "1712988043") rather than a JSON number,
// so numeric fields need to tolerate string-encoded values the way Python's int()/float() do.
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class FroniusMeterReading
{
    [JsonPropertyName("e_consumed")]
    public double EConsumed { get; set; }

    [JsonPropertyName("e_produced")]
    public double EProduced { get; set; }

    [JsonPropertyName("er_consumed")]
    public double ErConsumed { get; set; }

    [JsonPropertyName("er_produced")]
    public double ErProduced { get; set; }

    [JsonPropertyName("f")]
    public double Frequency { get; set; }

    [JsonPropertyName("u1")]
    public double U1 { get; set; }

    [JsonPropertyName("u2")]
    public double U2 { get; set; }

    [JsonPropertyName("u3")]
    public double U3 { get; set; }

    [JsonPropertyName("i1")]
    public double I1 { get; set; }

    [JsonPropertyName("i2")]
    public double I2 { get; set; }

    [JsonPropertyName("i3")]
    public double I3 { get; set; }

    [JsonPropertyName("p1")]
    public double P1 { get; set; }

    [JsonPropertyName("p2")]
    public double P2 { get; set; }

    [JsonPropertyName("p3")]
    public double P3 { get; set; }

    [JsonPropertyName("pt")]
    public double PTotal { get; set; }

    [JsonPropertyName("pr1")]
    public double Pr1 { get; set; }

    [JsonPropertyName("pr2")]
    public double Pr2 { get; set; }

    [JsonPropertyName("pr3")]
    public double Pr3 { get; set; }

    [JsonPropertyName("prt")]
    public double PrTotal { get; set; }

    [JsonPropertyName("pa1")]
    public double Pa1 { get; set; }

    [JsonPropertyName("pa2")]
    public double Pa2 { get; set; }

    [JsonPropertyName("pa3")]
    public double Pa3 { get; set; }

    [JsonPropertyName("pat")]
    public double PaTotal { get; set; }

    [JsonPropertyName("pf1")]
    public double Pf1 { get; set; }

    [JsonPropertyName("pf2")]
    public double Pf2 { get; set; }

    [JsonPropertyName("pf3")]
    public double Pf3 { get; set; }

    [JsonPropertyName("pft")]
    public double PfTotal { get; set; }

    [JsonPropertyName("last_updated")]
    public long LastUpdated { get; set; }
}
