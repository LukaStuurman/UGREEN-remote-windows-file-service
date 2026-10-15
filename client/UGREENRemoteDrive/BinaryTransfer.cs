namespace UGREENRemoteDrive;

internal static class BinaryTransfer
{
    internal const int MaximumLength = 4 * 1024 * 1024;

    internal static int Capacity(string? readLength, int? writeLength, bool binaryResponse)
    {
        if (writeLength is < 0 or > MaximumLength ||
            (binaryResponse && (!int.TryParse(readLength, out var count) || count < 0 || count > MaximumLength)))
            throw new ArgumentOutOfRangeException(nameof(readLength));
        if (binaryResponse && writeLength is not null) throw new ArgumentException("Duplex binary requests are not supported.");
        return Math.Max(1, binaryResponse ? int.Parse(readLength!) : writeLength ?? 0);
    }

    internal static byte[] Read(Stream stream, int length, int capacity)
    {
        if (length < 0 || length > capacity || capacity > MaximumLength)
            throw new IOException("Ongeldige lengte van het binaire browserantwoord.");
        var data = new byte[length];
        stream.ReadExactly(data);
        return data;
    }
}
