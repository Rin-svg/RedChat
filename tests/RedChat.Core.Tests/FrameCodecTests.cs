using Xunit;

namespace RedChat.Core.Tests;

public class FrameCodecTests
{

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        using MemoryStream ms = new();
        byte[] payload = { 1, 2, 3, 4, 5 };
        await FrameCodec.WriteFrameAsync(ms, payload, CancellationToken.None);
        ms.Position = 0;
        byte[] back = (await FrameCodec.ReadFrameAsync(ms, CancellationToken.None))!;
        Assert.Equal(5, back.Length);
        Assert.Equal((byte)5, back[4]);
    }

    [Fact]
    public async Task Read_CleanEndOfStream_ReturnsNull()
    {
        using MemoryStream ms = new();
        Assert.Null(await FrameCodec.ReadFrameAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task Read_OversizedLength_RejectedBeforeAllocation()
    {
        // Longueur annoncée : 0xFFFFFFFF (4 Gio) avec seulement 4 octets réellement présents.
        using MemoryStream ms = new(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task Read_LengthJustAboveMax_Rejected_AndMaxAccepted()
    {
        int tooBig = ProtocolLimits.MaxFrameBytes + 1;
        using MemoryStream bad = new(new byte[] { (byte)(tooBig >> 24), (byte)(tooBig >> 16), (byte)(tooBig >> 8), (byte)tooBig });
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(bad, CancellationToken.None));

        using MemoryStream ok = new();
        await FrameCodec.WriteFrameAsync(ok, new byte[ProtocolLimits.MaxFrameBytes], CancellationToken.None);
        ok.Position = 0;
        Assert.Equal(ProtocolLimits.MaxFrameBytes, (await FrameCodec.ReadFrameAsync(ok, CancellationToken.None))!.Length);
    }

    [Fact]
    public async Task Read_ZeroLength_Rejected()
    {
        using MemoryStream ms = new(new byte[] { 0, 0, 0, 0 });
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task Read_TruncatedHeaderOrPayload_Rejected()
    {
        using MemoryStream header = new(new byte[] { 0, 0 });
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(header, CancellationToken.None));
        using MemoryStream payload = new(new byte[] { 0, 0, 0, 10, 1, 2, 3 });
        await Assert.ThrowsAsync<ProtocolViolationException>(() => FrameCodec.ReadFrameAsync(payload, CancellationToken.None));
    }

    [Fact]
    public async Task Write_EmptyOrTooLarge_Throws()
    {
        using MemoryStream ms = new();
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => FrameCodec.WriteFrameAsync(ms, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => FrameCodec.WriteFrameAsync(ms, new byte[ProtocolLimits.MaxFrameBytes + 1], CancellationToken.None));
    }
}
