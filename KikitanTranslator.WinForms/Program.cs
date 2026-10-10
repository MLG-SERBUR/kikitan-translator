// FORK (feed-overlay): native WinForms entry point. No webview.

using KikitanTranslator.Base;
using KikitanTranslator.Utility;
using Velopack;

namespace KikitanTranslator.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        VelopackApp.Build().Run();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Logger.Initialize();
        AppConfig.Load();

        ApplicationConfiguration.Initialize();
        using var engine = new AppEngine();
        engine.Initialize();
        Application.Run(new MainForm(engine));
    }
}
