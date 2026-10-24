using System.Buffers.Binary;

namespace Kubico.Domain.Chores;

/// <summary>
/// Tira os metadados das fotos de prova (EXIF com a localização GPS e o modelo do telemóvel, XMP, IPTC, textos),
/// sem bibliotecas de imagem: no JPEG os segmentos APP1 e APP13; no PNG os blocos eXIf, tEXt, iTXt e zTXt; no WebP
/// os blocos EXIF e "XMP " (e os avisos deles no VP8X). O resto da imagem fica igual. Confirma também o início da
/// estrutura (um ficheiro com 3 bytes de JPEG à frente e HTML a seguir não passa); daí em diante, se a estrutura
/// estiver estragada, o resto fica como veio.
/// </summary>
public static class PhotoMetadata
{
    /// <summary>A foto sem metadados, ou null se não for um JPEG, PNG ou WebP com um início válido.</summary>
    public static byte[]? Strip(byte[] data, string contentType) => contentType switch
    {
        "image/jpeg" => StripJpeg(data),
        "image/png" => StripPng(data),
        "image/webp" => StripWebp(data),
        _ => null,
    };

    private static byte[]? StripJpeg(byte[] data)
    {
        // SOI e, logo a seguir, um marcador a sério (FFC0–FFFE).
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF || data[3] < 0xC0)
        {
            return null;
        }

        var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var pos = 2;
        while (pos < data.Length)
        {
            if (pos + 1 >= data.Length || data[pos] != 0xFF)
            {
                break;
            }

            var marker = data[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }

            // Marcadores sem tamanho (RSTn, TEM, EOI).
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7) or 0xD9)
            {
                output.Write(data, pos, 2);
                pos += 2;
                if (marker == 0xD9)
                {
                    return output.ToArray();
                }

                continue;
            }

            // A partir do início dos dados da imagem (SOS), copia-se tudo.
            if (marker == 0xDA || pos + 4 > data.Length)
            {
                break;
            }

            var length = (data[pos + 2] << 8) | data[pos + 3];
            if (length < 2 || pos + 2 + length > data.Length)
            {
                break;
            }

            // APP1 (EXIF, XMP) e APP13 (IPTC, Photoshop) saem; o resto fica.
            if (marker is not (0xE1 or 0xED))
            {
                output.Write(data, pos, 2 + length);
            }

            pos += 2 + length;
        }

        output.Write(data, pos, data.Length - pos);
        return output.ToArray();
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly string[] PngMetadataChunks = ["eXIf", "tEXt", "iTXt", "zTXt"];

    private static byte[]? StripPng(byte[] data)
    {
        // A assinatura e, a seguir, o bloco IHDR.
        if (data.Length < 16 || !data.AsSpan(0, 8).SequenceEqual(PngSignature) || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var output = new MemoryStream(data.Length);
        output.Write(data, 0, 8);
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            if (length > int.MaxValue - 12 || pos + 12 + (long)length > data.Length)
            {
                break;
            }

            var total = 12 + (int)length;
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (!PngMetadataChunks.Contains(type))
            {
                output.Write(data, pos, total);
            }

            pos += total;
            if (type == "IEND")
            {
                // O que vier depois do fim da imagem não interessa (e podia esconder outras coisas).
                return output.ToArray();
            }
        }

        output.Write(data, pos, data.Length - pos);
        return output.ToArray();
    }

    private static byte[]? StripWebp(byte[] data)
    {
        // RIFF…WEBP e o primeiro bloco a ser de imagem (VP8, VP8L ou VP8X).
        if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WEBP"u8)
            || !(data.AsSpan(12, 4).SequenceEqual("VP8 "u8) || data.AsSpan(12, 4).SequenceEqual("VP8L"u8)
                || data.AsSpan(12, 4).SequenceEqual("VP8X"u8)))
        {
            return null;
        }

        var output = new MemoryStream(data.Length);
        output.Write(data, 0, 12);
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            var padded = (long)size + (size % 2);
            if (pos + 8 + padded > data.Length)
            {
                break;
            }

            var total = 8 + (int)padded;
            var type = System.Text.Encoding.ASCII.GetString(data, pos, 4);
            if (type is not ("EXIF" or "XMP "))
            {
                var start = (int)output.Position;
                output.Write(data, pos, total);
                if (type == "VP8X" && size >= 1)
                {
                    // Os avisos "há EXIF" e "há XMP" do VP8X deixam de valer.
                    var bytes = output.GetBuffer();
                    bytes[start + 8] &= unchecked((byte)~0x0C);
                }
            }

            pos += total;
        }

        output.Write(data, pos, data.Length - pos);
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        return result;
    }
}
