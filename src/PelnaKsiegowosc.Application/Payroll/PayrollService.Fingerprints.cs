using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PelnaKsiegowosc.Application.Payroll;

public sealed partial class PayrollService
{
    // Numeric scale differs between SQLite and PostgreSQL (6000 vs 6000.00).
    // Approval fingerprints bind numeric values, not their provider-specific scale.
    private static readonly JsonSerializerOptions FingerprintJson = new()
    {
        Converters = { new CanonicalDecimalConverter() }
    };
    private sealed class CanonicalDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, System.Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture));
    }
}
