using System.Text;
using NetworkToys.Core.Tftp;
using Xunit;

namespace NetworkToys.Core.Tests;

public class TftpPacketTests
{
    private static byte[] BuildRequest(TftpOpcode opcode, string filename, string mode, params string[] options)
    {
        var bytes = new List<byte> { 0, (byte)opcode };
        bytes.AddRange(Encoding.ASCII.GetBytes(filename)); bytes.Add(0);
        bytes.AddRange(Encoding.ASCII.GetBytes(mode)); bytes.Add(0);
        for (int i = 0; i + 1 < options.Length; i += 2)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(options[i])); bytes.Add(0);
            bytes.AddRange(Encoding.ASCII.GetBytes(options[i + 1])); bytes.Add(0);
        }
        return [.. bytes];
    }

    [Fact]
    public void A_read_request_is_parsed()
    {
        TftpRequest? request = TftpPacket.ReadRequest(BuildRequest(TftpOpcode.ReadRequest, "running-config", "OCTET"));

        Assert.NotNull(request);
        Assert.Equal("running-config", request.Value.Filename);
        Assert.Equal("octet", request.Value.Mode);   // 小文字化される
    }

    [Fact]
    public void Request_options_are_read()
    {
        TftpRequest? request = TftpPacket.ReadRequest(
            BuildRequest(TftpOpcode.WriteRequest, "img.bin", "octet", "blksize", "1468", "tsize", "0"));

        Assert.NotNull(request);
        Assert.Equal("1468", request.Value.Options["blksize"]);
        Assert.Equal("0", request.Value.Options["tsize"]);
    }

    [Theory]
    [InlineData(new byte[] { 0 })]              // 短すぎ
    [InlineData(new byte[] { 0, 9 })]           // 未知の opcode
    [InlineData(new byte[] { 0, 1, 0x61 })]     // NUL が足りない
    public void A_broken_request_is_null(byte[] packet)
    {
        Assert.Null(TftpPacket.ReadRequest(packet));
    }

    [Fact]
    public void Data_round_trips()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] packet = TftpPacket.Data(300, payload);

        Assert.Equal(TftpOpcode.Data, TftpPacket.OpcodeOf(packet));
        Assert.Equal((ushort)300, TftpPacket.ReadDataBlock(packet));
        Assert.True(TftpPacket.ReadDataPayload(packet).SequenceEqual(payload));
    }

    [Fact]
    public void Ack_round_trips()
    {
        byte[] packet = TftpPacket.Ack(1);

        Assert.Equal(TftpOpcode.Ack, TftpPacket.OpcodeOf(packet));
        Assert.Equal((ushort)1, TftpPacket.ReadAckBlock(packet));
    }

    [Fact]
    public void The_block_number_wraps_as_an_unsigned_16bit()
    {
        // 大きなファイルでブロック番号が 65535 を超えると 0 へ回る
        byte[] packet = TftpPacket.Data(ushort.MaxValue, [0]);
        Assert.Equal(ushort.MaxValue, TftpPacket.ReadDataBlock(packet));
    }

    [Fact]
    public void An_error_packet_carries_code_and_message()
    {
        byte[] packet = TftpPacket.Error(TftpError.FileNotFound, "no such file");

        Assert.Equal(TftpOpcode.Error, TftpPacket.OpcodeOf(packet));
        Assert.Equal((byte)TftpError.FileNotFound, packet[3]);
        Assert.Equal(0, packet[^1]);   // NUL 終端
    }

    [Fact]
    public void An_option_ack_lists_the_accepted_options()
    {
        byte[] packet = TftpPacket.OptionAck(new Dictionary<string, string> { ["blksize"] = "1468" });

        Assert.Equal(TftpOpcode.OptionAck, TftpPacket.OpcodeOf(packet));
        Assert.Contains("blksize", Encoding.ASCII.GetString(packet), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TftpOpcode.ReadRequest)]
    [InlineData(TftpOpcode.WriteRequest)]
    public void A_request_round_trips_through_the_reader(TftpOpcode opcode)
    {
        byte[] packet = TftpPacket.Request(
            opcode, "running-config.txt", "octet",
            new Dictionary<string, string> { ["blksize"] = "1468", ["tsize"] = "0" });

        Assert.Equal(opcode, TftpPacket.OpcodeOf(packet));

        TftpRequest? read = TftpPacket.ReadRequest(packet);
        Assert.NotNull(read);
        Assert.Equal("running-config.txt", read!.Value.Filename);
        Assert.Equal("octet", read.Value.Mode);
        Assert.Equal("1468", read.Value.Options["blksize"]);
        Assert.Equal("0", read.Value.Options["tsize"]);
    }

    // 組み立て側を後から足したので、それまで手で組んでいた形と 1 バイトも
    // 違わないことを固定する（違えば、これまでの検査が別物を見ていたことになる）
    [Fact]
    public void The_builder_produces_the_same_bytes_the_tests_built_by_hand()
    {
        byte[] byHand = BuildRequest(TftpOpcode.ReadRequest, "startup.cfg", "octet", "blksize", "512");
        byte[] built = TftpPacket.Request(
            TftpOpcode.ReadRequest, "startup.cfg", "octet",
            new Dictionary<string, string> { ["blksize"] = "512" });

        Assert.Equal(byHand, built);
    }

    [Fact]
    public void A_request_without_options_ends_after_the_mode()
    {
        byte[] packet = TftpPacket.Request(TftpOpcode.WriteRequest, "a.txt");

        Assert.Equal(BuildRequest(TftpOpcode.WriteRequest, "a.txt", "octet"), packet);
        Assert.Empty(TftpPacket.ReadRequest(packet)!.Value.Options);
    }

    [Fact]
    public void Only_rrq_and_wrq_can_be_built_as_requests()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TftpPacket.Request(TftpOpcode.Data, "a.txt"));
}
