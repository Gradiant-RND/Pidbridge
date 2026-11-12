using System.IO;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace PidBridgeDrawing;

public static class DrawingFeedback
{
    public static string LogPath => Path.Combine(Path.GetTempPath(), "PidBridge",
        $"import-{Environment.ProcessId}.log");

    public static void Notify(string message, string? detail = null, bool forceAlert = false)
    {
        // Constructing or using any diagnostic sink is contained here too.
        try
        {
            var reporter = new ImportFeedback(
                text =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
                },
                text =>
                {
                    var doc = AcApp.DocumentManager.MdiActiveDocument;
                    if (doc == null) throw new InvalidOperationException("Nessun documento attivo per il messaggio.");
                    doc.Editor.WriteMessage("\n" + text);
                },
                text => AcApp.ShowAlertDialog(text));
            reporter.Notify(message, detail, forceAlert);
        }
        catch (System.Exception) { /* No diagnostic failure may escape a command handler. */ }
    }
    public static void Error(System.Exception error, string phase, string? input, string? output)
    {
        Notify("Importazione interrotta nella fase: " + phase + "\n" + error.Message
            + "\nLog completo: " + LogPath
            + "\nSe il DWG risultante e gia presente, non considerarlo verificato senza un messaggio di successo.",
            "Input: " + input + "\nOutput: " + output + "\n" + error, true);
    }
}
