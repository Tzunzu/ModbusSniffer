using Xunit;
using Sut = ModbusSniffer.Program;

namespace ModbusSniffer.Tests;

// Exercises the frame-classification heuristics that give the sniffer its value.
// Sample frames are real captures pulled from a session log: an 8-byte FC03 read
// request and the matching 93-byte FC03 response (all-zero registers), both
// CRC-valid as recorded.
public class ModbusFrameTests
{
    private const string RequestFc03Hex = "64030000002C4DE2";
    private static readonly byte[] RequestFc03 = Convert.FromHexString(RequestFc03Hex);

    // 15-byte header + 76 zero register bytes + 2-byte CRC = 93 bytes.
    private static readonly byte[] ResponseFc03 =
        Convert.FromHexString("5B0358419E09F443C28000428F23A6" + new string('0', 152) + "7768");

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    [Theory]
    [InlineData("64030000002C4DE2", "REQUEST")]          // FC03 read, 8-byte master request
    [InlineData("0103030000000000", "AMBIGUOUS")]        // FC03, 8 bytes where byteCount + 5 == length
    [InlineData("0110000000020000", "RESPONSE")]         // FC10 write-multiple, 8-byte slave echo
    [InlineData("01100000000204DEADBEEF0000", "REQUEST")]// FC10, length == byteCount + 9
    [InlineData("0106000000000000", "AMBIGUOUS")]        // FC06 layouts overlap in both directions
    [InlineData("018302C0F1", "RESPONSE")]               // exception response, function | 0x80
    [InlineData("01070000", "REQUEST")]                  // FC07 Read Exception Status, 4-byte request
    [InlineData("0107000000", "RESPONSE")]               // FC07, 5-byte response
    [InlineData("010B0000", "REQUEST")]                  // FC0B Get Comm Event Counter request
    [InlineData("010B000000000000", "RESPONSE")]         // FC0B, 8-byte response
    [InlineData("01110000", "REQUEST")]                  // FC11 Report Server ID request
    [InlineData("011800000000", "REQUEST")]              // FC18 Read FIFO Queue, 6-byte request
    [InlineData("01180000000000", "RESPONSE")]           // FC18, non-6-byte response
    [InlineData("0108000000000000", "AMBIGUOUS")]        // FC08 Diagnostics echoes the request
    [InlineData("012B0E04000000", "REQUEST")]            // FC2B Encapsulated Interface Transport request
    [InlineData("0109000000", "UNKNOWN")]                // FC09 has no defined layout
    [InlineData("01", "INCOMPLETE")]                     // fewer than two bytes
    public void GetModbusDirection_classifies_frame(string hex, string expected) =>
        Assert.Equal(expected, Sut.GetModbusDirection(Bytes(hex)));

    [Theory]
    [InlineData(0x03, "Read Holding Registers")]
    [InlineData(0x10, "Write Multiple Registers")]
    [InlineData(0x2B, "Encapsulated Interface Transport")]
    [InlineData(0x83, "Read Holding Registers exception")]
    [InlineData(0x64, "Unknown function 0x64")]
    public void FunctionName_maps_code_to_name(int code, string expected) =>
        Assert.Equal(expected, Sut.FunctionName((byte)code));

    [Fact]
    public void GetModbusDirection_reads_long_response_as_response() =>
        Assert.Equal("RESPONSE", Sut.GetModbusDirection(ResponseFc03));

    [Fact]
    public void HasValidModbusCrc_accepts_captured_request() =>
        Assert.True(Sut.HasValidModbusCrc(RequestFc03, RequestFc03.Length));

    [Fact]
    public void HasValidModbusCrc_accepts_captured_response() =>
        Assert.True(Sut.HasValidModbusCrc(ResponseFc03, ResponseFc03.Length));

    [Fact]
    public void HasValidModbusCrc_rejects_corrupted_payload()
    {
        byte[] corrupted = (byte[])RequestFc03.Clone();
        corrupted[3] ^= 0xFF;

        Assert.False(Sut.HasValidModbusCrc(corrupted, corrupted.Length));
    }

    [Fact]
    public void HasValidModbusCrc_rejects_wrong_length() =>
        Assert.False(Sut.HasValidModbusCrc(RequestFc03, RequestFc03.Length - 1));

    [Theory]
    [InlineData("0103", new[] { 8 })]                 // FC03 read request
    [InlineData("010358", new[] { 8, 93 })]           // FC03 with a byte count of 0x58
    [InlineData("0183", new[] { 5 })]                 // exception response
    [InlineData("0107", new[] { 4, 5 })]              // FC07 request or response
    [InlineData("010B", new[] { 4, 8 })]              // FC0B request or response
    [InlineData("0108", new[] { 8 })]                 // FC08 diagnostics echo
    [InlineData("0111", new[] { 4 })]                 // FC11 request (no byte count yet)
    [InlineData("01180040", new[] { 6, 70 })]         // FC18 request or 16-bit byte-count response
    [InlineData("0109", new int[0])]                  // undefined function code
    public void GetPossibleFrameLengths_matches_function_layout(string hex, int[] expected) =>
        Assert.Equal(expected, Sut.GetPossibleFrameLengths(Bytes(hex)));

    [Fact]
    public void GetPossibleFrameLengths_covers_echo_and_full_write()
    {
        int[] lengths = Sut.GetPossibleFrameLengths(Bytes("01100000000204")).ToArray();

        Assert.Contains(8, lengths);
        Assert.Contains(13, lengths);
    }

    [Fact]
    public void TryExtractModbusFrame_pulls_single_frame_and_drains_buffer()
    {
        var buffer = new List<byte>(RequestFc03);

        Assert.True(Sut.TryExtractModbusFrame(buffer, out byte[]? frame));
        Assert.Equal(RequestFc03, frame);
        Assert.Empty(buffer);
    }

    [Fact]
    public void TryExtractModbusFrame_pulls_request_then_response()
    {
        var buffer = new List<byte>();
        buffer.AddRange(RequestFc03);
        buffer.AddRange(ResponseFc03);

        Assert.True(Sut.TryExtractModbusFrame(buffer, out byte[]? first));
        Assert.Equal(8, first!.Length);
        Assert.Equal("REQUEST", Sut.GetModbusDirection(first));

        Assert.True(Sut.TryExtractModbusFrame(buffer, out byte[]? second));
        Assert.Equal(93, second!.Length);
        Assert.Equal("RESPONSE", Sut.GetModbusDirection(second));

        Assert.Empty(buffer);
    }

    [Fact]
    public void TryExtractModbusFrame_leaves_unrecognised_bytes_untouched()
    {
        var buffer = new List<byte> { 0x00, 0x01, 0x02, 0x03 };

        Assert.False(Sut.TryExtractModbusFrame(buffer, out byte[]? frame));
        Assert.Null(frame);
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void TryFindNextRequestStart_finds_request_after_leading_noise()
    {
        var buffer = new List<byte> { 0xFF };
        buffer.AddRange(RequestFc03);

        Assert.True(Sut.TryFindNextRequestStart(buffer, out int start));
        Assert.Equal(1, start);
    }

    [Fact]
    public void TryFindNextRequestStart_ignores_request_already_at_offset_zero()
    {
        var buffer = new List<byte>(RequestFc03);

        Assert.False(Sut.TryFindNextRequestStart(buffer, out int start));
        Assert.Equal(0, start);
    }

    [Fact]
    public void TryFindNextRequestStart_returns_false_for_pure_noise()
    {
        var buffer = new List<byte> { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

        Assert.False(Sut.TryFindNextRequestStart(buffer, out _));
    }
}
