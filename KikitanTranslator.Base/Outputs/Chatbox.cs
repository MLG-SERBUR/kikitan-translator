using System.Net;
using System.Net.Sockets;
using System.Text;
using KikitanTranslator.Utility;
using Serilog;

namespace KikitanTranslator.Base.Outputs;

public class Chatbox : IOutput
{
    // FORK: VRChat listens on UDP 9000 (hardcoded on their end). We build raw
    // OSC packets over UdpClient instead of VRCOscLib's OscChatbox: its
    // OscParameter static initializer tries to bind a receive server, which
    // throws WSAEACCES and kills every send before the message goes out.
    private static readonly IPEndPoint _endpoint = new(IPAddress.Loopback, 9000);

    public void Send(string recognized, string translated, bool final)
    {
        try
        {
            SendTyping(!final);
            if (!final && !AppConfig.ConfigObject.SendWithoutWaitingForFinish) return;

            string message;
            if (AppConfig.ConfigObject.SpeechToTextOnly) message = recognized;
            else if (AppConfig.ConfigObject.TranslationOnly) message = translated;
            else message = $"{translated} ({recognized})";

            // /chatbox/input (string text, bool direct, bool complete)
            using var client = new UdpClient();
            client.Send(BuildMessage("/chatbox/input", message, true, true), _endpoint);
            Log.Debug($"[OSC]  Chatbox send completed: final={final}, sourceChars={recognized.Length}, translationChars={translated.Length}");
        }
        catch (Exception e)
        {
            Log.Error(e, "[OSC]  Error sending OSC message");
        }
    }

    private static void SendTyping(bool typing)
    {
        // /chatbox/typing (bool typing)
        using var client = new UdpClient();
        client.Send(BuildMessage("/chatbox/typing", typing), _endpoint);
    }

    private static byte[] BuildMessage(string address, params object[] args)
    {
        using var ms = new MemoryStream();
        WritePaddedString(ms, address);
        var tags = "," + string.Concat(args.Select(a => a switch
        {
            string => "s",
            bool b => b ? "T" : "F",
            _ => throw new ArgumentException($"Unsupported OSC arg type: {a.GetType()}")
        }));
        WritePaddedString(ms, tags);
        foreach (var s in args.OfType<string>())
            WritePaddedString(ms, s);
        // bool args carry no payload; encoded in the type tag only.
        return ms.ToArray();
    }

    private static void WritePaddedString(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        ms.Write(bytes, 0, bytes.Length);
        ms.WriteByte(0);
        var pad = (4 - ms.Length % 4) % 4;
        for (var i = 0; i < pad; i++) ms.WriteByte(0);
    }

    public bool IsDelayed() => true;
}
