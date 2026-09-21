namespace Trainingify.Services;

public static class AntHeartRateProtocol
{
    public static bool TryParseAddress(string address, out ushort deviceNumber)
    {
        deviceNumber = 0;
        return address.StartsWith("ANT:", StringComparison.Ordinal)
            && ushort.TryParse(address.AsSpan(4), out deviceNumber) && deviceNumber > 0;
    }

    public static byte[] Frame(byte id, byte[] data)
    {
        if (data.Length > 255) throw new ArgumentOutOfRangeException(nameof(data));
        var frame = new byte[data.Length + 4];
        frame[0] = 0xA4;
        frame[1] = (byte)data.Length;
        frame[2] = id;
        data.CopyTo(frame, 3);
        for (var i = 0; i < frame.Length - 1; i++) frame[^1] ^= frame[i];
        return frame;
    }

    public static bool TryRead(List<byte> pending, out byte id, out byte[] data)
    {
        id = 0; data = [];
        while (pending.Count > 0)
        {
            if (pending[0] != 0xA4) { pending.RemoveAt(0); continue; }
            if (pending.Count < 4) return false;
            var length = pending[1] + 4;
            if (pending.Count < length) return false;
            byte checksum = 0;
            for (var i = 0; i < length; i++) checksum ^= pending[i];
            if (checksum != 0) { pending.RemoveAt(0); continue; }
            id = pending[2];
            data = pending.GetRange(3, pending[1]).ToArray();
            pending.RemoveRange(0, length);
            return true;
        }
        return false;
    }
}
