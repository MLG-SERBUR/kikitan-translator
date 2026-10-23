using BuildSoft.VRChat.Osc;
using BuildSoft.VRChat.Osc.Chatbox;
using KikitanTranslator.Utility;
using Serilog;

namespace KikitanTranslator.Base.Outputs;

public class Chatbox : IOutput
{
    // FORK: VRChat listens on UDP 9000, hardcoded on their end and not
    // configurable. The old configurable port only served to silently break
    // the chatbox, so it is fixed here.
    public Chatbox() => OscConnectionSettings.SendPort = 9000;
    
    public void Send(string recognized, string translated, bool final)
    {
        try
        {
            OscChatbox.SetIsTyping(!final);
            if (!final && !AppConfig.ConfigObject.SendWithoutWaitingForFinish) return;
            
            if (AppConfig.ConfigObject.SpeechToTextOnly) OscChatbox.SendMessage(recognized, true);
            else if (AppConfig.ConfigObject.TranslationOnly) OscChatbox.SendMessage(translated, true);
            else OscChatbox.SendMessage(FormatMessage(recognized, translated), true);
            Log.Debug($"[OSC]  Chatbox send completed: final={final}, sourceChars={recognized.Length}, translationChars={translated.Length}");
        } catch (Exception e)
        {
            Log.Error($"[OSC]  Error sending OSC message! Reason: {e}");
        }
    }

    // FORK: in the chatbox, inline parentheses around RTL text break the bidi
    // ordering. When either side is RTL, put the transcription on its own
    // line instead, no parentheses.
    private static string FormatMessage(string recognized, string translated)
        => IsRtl(recognized) || IsRtl(translated)
            ? $"{translated}\n{recognized}"
            : $"{translated} ({recognized})";

    private static bool IsRtl(string text) => text.Any(c => c is >= '\u0590' and <= '\u08FF');

    public bool IsDelayed() => true;
}