using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(PidBridgeDrawing.DrawingCommands))]
[assembly: CommandClass(typeof(PidBridgeDrawing.DrawingCommands))]
namespace PidBridgeDrawing;

public sealed class DrawingCommands : IExtensionApplication
{
    public void Initialize()=>Version();
    public void Terminate() { }
    [CommandMethod("PIDVERSIONDISEGNO9",CommandFlags.Modal)]
    public void Version()=>DrawingFeedback.Notify(
        "\nPidBridge DISEGNO COMPLETO 1.2.9. Comandi: PIDEXPORT e PIDIMPORT."
        +"\nDLL: "+typeof(DrawingCommands).Assembly.Location);

    [CommandMethod("PIDEXPORT",CommandFlags.Modal)]
    public void Export()
    {
        var doc=AcApp.DocumentManager.MdiActiveDocument;if(doc==null)return;
        try
        {
            string source=doc.Database.Filename;
            if(string.IsNullOrWhiteSpace(source) || !File.Exists(source) || Convert.ToInt32(AcApp.GetSystemVariable("DBMOD"))!=0)
                throw new InvalidDataException("Salvare prima il DWG (SALVAVEL/_QSAVE), poi ripetere PIDEXPORT. Il JSON deve contenere esattamente lo stato salvato.");
            using var dialog=new SaveFileDialog { Title="Esporta l'intero disegno in JSON",Filter="Disegno JSON (*.json)|*.json",
                FileName=Path.GetFileNameWithoutExtension(source)+"-originale.json",OverwritePrompt=true };
            if(dialog.ShowDialog()!=DialogResult.OK)return;
            if(new FileInfo(source).Length>DrawingProtocol.MaxNativeBytes)throw new InvalidDataException("DWG oltre 64 MiB: formato completo attuale non applicabile.");
            var bytes=DrawingFile.ReadSavedDwg(source);
            var native=DrawingSnapshot.Embed(bytes);
            using var snapshot=DrawingSnapshot.OpenEmbedded(bytes);
            var previous=HostApplicationServices.WorkingDatabase;
            FullDrawing state;
            try { HostApplicationServices.WorkingDatabase=snapshot;state=DrawingSnapshot.Read(snapshot,native,Path.GetFullPath(source),Path.GetFileName(source)); }
            finally { HostApplicationServices.WorkingDatabase=previous; }
            DrawingProtocol.Validate(state);
            string json=JsonSerializer.Serialize(state,DrawingProtocol.Json);
            if(System.Text.Encoding.UTF8.GetByteCount(json)>DrawingProtocol.MaxJsonBytes)throw new InvalidDataException("Esportazione oltre 128 MiB.");
            AtomicWrite(dialog.FileName,json);
            DrawingFeedback.Notify($"\nJSON COMPLETO creato: {dialog.FileName}\n{state.Entities.Length} entita, {state.Owners.Length} definizioni/spazi, DWG nativo incorporato."
                +"\nPassare questo file all'AI. Il JSON modificato dovra mantenere tutte le entita non interessate e il campo native invariato."
                +"\nIl DWG aperto non e stato modificato.");
        }
        catch(System.Exception ex){DrawingFeedback.Notify("\nEsportazione completa interrotta: "+ex.Message,ex.ToString(),true);}
    }

    [CommandMethod("PIDIMPORT",CommandFlags.Session)]
    public void Import()
    {
        string phase="selezione del JSON";
        string? input=null;
        string? output=null;
        try
        {
            using var open=new OpenFileDialog { Title="Importa il JSON dell'intero disegno modificato",Filter="Disegno JSON (*.json)|*.json" };
            if(open.ShowDialog()!=DialogResult.OK)return;
            input=open.FileName;
            phase="lettura e validazione del JSON";
            var desired=DrawingProtocol.Load(input);
            phase="lettura del DWG incorporato e confronto baseline";
            using(var db=DrawingSnapshot.OpenEmbedded(DrawingProtocol.DecodeNative(desired.Native)))
            {
            var previous=HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase=db;
                var baseline=DrawingSnapshot.Read(db,desired.Native,desired.OriginalPath,desired.DrawingName);
                DrawingProtocol.ValidateAgainstBaseline(baseline,desired);
                var diff=DrawingProtocol.Diff(baseline,desired);
                var nativeOnlyCopies=desired.Entities.Count(e=>e.SourceHandle!=null && e.Id!=e.SourceHandle && e.Geometry.TryGetProperty("nativeOnly",out var n) && n.ValueKind==JsonValueKind.True);
                var text=$"DISEGNO COMPLETO: {desired.DrawingName}\r\n"
                    +$"Entita native iniziali: {baseline.Entities.Length}\r\nEntita nello stato finale JSON: {desired.Entities.Length}\r\n"
                    +$"Invariate: {diff.Unchanged}\r\nModificate: {diff.Changed}\r\nCopie: {diff.Copies}\r\nNuove: {diff.Created}\r\nRimosse: {diff.Deleted}\r\n"
                    +$"Riferimenti parte nuovi: {desired.Entities.Count(e=>e.Type=="MechanicalPartReference")}\r\nCopie di oggetti nativi opachi: {nativeOnlyCopies}";
                if(!Preview(text))return;
                using var save=new SaveFileDialog { Title="Salva il nuovo DWG risultante",Filter="Disegno AutoCAD (*.dwg)|*.dwg",
                    FileName=Path.GetFileNameWithoutExtension(desired.DrawingName)+"_modificato.dwg",OverwritePrompt=true };
                string? originalDirectory=Path.GetDirectoryName(desired.OriginalPath);
                if(originalDirectory!=null && Directory.Exists(originalDirectory))save.InitialDirectory=originalDirectory;
                if(save.ShowDialog()!=DialogResult.OK)return;
                output=Path.GetFullPath(save.FileName);
                if(File.Exists(output))throw new InvalidDataException("Scegliere un nuovo nome DWG: l'importazione non sovrascrive file esistenti.");
                // Apply after the preview database has been closed.


            }
            finally { HostApplicationServices.WorkingDatabase=previous; }
            }
            DrawingImport.CreateResult(desired,output!,value=>phase=value);
            DrawingFeedback.Notify("\nDisegno completo importato e verificato in: "+output);
        }
        catch(System.Exception ex)
        {
            DrawingFeedback.Error(ex,phase,input,output);
        }

    }
    private static void AtomicWrite(string destination,string text)
    {
        string temp=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,"pidbridge-json-"+Guid.NewGuid().ToString("N")+".tmp");
        try { File.WriteAllText(temp,text,new System.Text.UTF8Encoding(false));File.Move(temp,destination,true); }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    private static bool Preview(string text)
    {
        using var form=new Form { Text="PidBridge - importa disegno completo",Width=1050,Height=300,StartPosition=FormStartPosition.CenterScreen };
        var body=new TextBox { Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Text=text };
        var footer=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=50,FlowDirection=System.Windows.Forms.FlowDirection.RightToLeft };
        var cancel=new Button { Text="Annulla",DialogResult=DialogResult.Cancel,Width=130 };
        footer.Controls.Add(cancel);form.CancelButton=cancel;
        footer.Controls.Add(new Button { Text="Crea il nuovo DWG",DialogResult=DialogResult.OK,Width=180 });
        form.Controls.Add(body);form.Controls.Add(footer);
        return AcApp.ShowModalDialog(form)==DialogResult.OK;
    }
}
