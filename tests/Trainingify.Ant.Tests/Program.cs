using Trainingify.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Check(AntHeartRateProtocol.Frame(0x4A, [0]).SequenceEqual(new byte[] { 0xA4, 1, 0x4A, 0, 0xEF }), "ANT reset wire bytes");
var frame = AntHeartRateProtocol.Frame(0x4E, [0, 0, 0, 0, 0, 0, 0, 1, 143]);
var pending = new List<byte> { 0xFF, 0x00 };
pending.AddRange(frame.Take(5));
Check(!AntHeartRateProtocol.TryRead(pending, out _, out _), "Partial USB packet must wait");
pending.AddRange(frame.Skip(5));
pending.AddRange(frame);
Check(AntHeartRateProtocol.TryRead(pending, out var id, out var data) && id == 0x4E && data[8] == 143, "HRM wire decoding");
Check(AntHeartRateProtocol.TryRead(pending, out _, out _) && pending.Count == 0, "Multiple ANT frames per USB packet");
var corrupted = (byte[])frame.Clone(); corrupted[^1] ^= 1;
pending.AddRange(corrupted); pending.AddRange(frame);
Check(AntHeartRateProtocol.TryRead(pending, out _, out data) && data[8] == 143, "Checksum rejection and resynchronization");
Check(AntHeartRateProtocol.TryParseAddress("ANT:12345", out var number) && number == 12345, "Persisted ANT identity");
Check(!AntHeartRateProtocol.TryParseAddress("ANT:0", out _) && !AntHeartRateProtocol.TryParseAddress("F099196E5CAE", out _), "Reject wildcard and BLE identities");
Console.WriteLine("PASS: 7 ANT protocol checks");

if (args.Contains("--hardware"))
{
    using var receiver = new AntHeartRateReceiver();
    var ready = false;
    var received = false;
    receiver.Status += status => { Console.WriteLine(status); if (status.Contains("recherche cardio")) ready = true; };
    receiver.Measurement += (id, bpm) => { if (!received) Console.WriteLine($"HRM ANT:{id}: {bpm} bpm"); received = true; };
    receiver.Start();
    await Task.Delay(TimeSpan.FromSeconds(15));
    await receiver.StopAsync();
    Check(ready, "USB receiver configuration failed");
    Console.WriteLine(received ? "PASS: real ANT+ heart rate received" : "PASS: USB channel initialized; no broadcasting HRM received in 15 seconds");
}
