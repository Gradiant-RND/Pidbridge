namespace PidBridgeDrawing;

// No Autodesk dependency: failure reporting must survive an unavailable editor.
public sealed class ImportFeedback(Action<string> log, Action<string> editor, Action<string> alert)
{
    public void Notify(string message, string? detail = null, bool forceAlert = false)
    {
        TryLog(message + (detail == null ? "" : Environment.NewLine + detail));
        bool written = false;
        try { editor(message); written = true; }
        catch (System.Exception ex) { TryLog("Editor notification failed: " + ex); }
        if (!written || forceAlert)
        {
            try { alert(message); }
            catch (System.Exception ex) { TryLog("Alert notification failed: " + ex); }
        }
    }
    private void TryLog(string text)
    {
        try { log(text); }
        catch (System.Exception) { /* Diagnostics must never replace the original failure. */ }
    }
}
