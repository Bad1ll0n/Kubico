using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kubico.Api.Common;

/// <summary>
/// Enums em JSON como texto ("Daily"), e só com valores que existem: um número ou um nome fora do enum (9, "Hourly")
/// é um pedido inválido (400), em vez de um valor estranho gravado na base de dados (que depois partia a app).
/// Também nas chaves dos dicionários (as permissões por área).
/// </summary>
public sealed class DefinedEnumConverter : JsonConverterFactory
{
    private readonly JsonStringEnumConverter _inner = new();

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(Checked<>).MakeGenericType(typeToConvert), _inner.CreateConverter(typeToConvert, options))!;

    private sealed class Checked<T>(JsonConverter inner) : JsonConverter<T>
        where T : struct, Enum
    {
        private readonly JsonConverter<T> _inner = (JsonConverter<T>)inner;

        private static readonly bool IsFlags = typeof(T).IsDefined(typeof(FlagsAttribute), inherit: false);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Check(_inner.Read(ref reader, typeToConvert, options));

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            _inner.Write(writer, value, options);

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Check(_inner.ReadAsPropertyName(ref reader, typeToConvert, options));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            _inner.WriteAsPropertyName(writer, value, options);

        private static T Check(T value) => IsFlags || Enum.IsDefined(value)
            ? value
            : throw new JsonException($"O valor {value} não existe em {typeof(T).Name}.");
    }
}
