using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;

namespace PidBridgeDrawing;

public static class DrawingImport
{
    public static Document CreateResult(FullDrawing desired,string output,Action<string>? progress=null)
    {
        output=Path.GetFullPath(output);
        if(File.Exists(output))throw new InvalidDataException("Scegliere un nuovo nome DWG: nessun file esistente viene sovrascritto.");
        string stage=Path.Combine(Path.GetDirectoryName(output)!,"pidbridge-stage-"+Guid.NewGuid().ToString("N")+".dwg");
        Document? temporary=null;var previousDocument=AcApp.DocumentManager.MdiActiveDocument;
        bool completed=false,written=false;
        try
        {
            progress?.Invoke("ricostruzione del DWG temporaneo");
            Dictionary<string,string> handles;
            using(var db=DrawingSnapshot.OpenEmbedded(DrawingProtocol.DecodeNative(desired.Native)))
            {
                var previous=HostApplicationServices.WorkingDatabase;
                try
                {
                    HostApplicationServices.WorkingDatabase=db;
                    var baseline=DrawingSnapshot.Read(db,desired.Native,desired.OriginalPath,desired.DrawingName);
                    DrawingProtocol.ValidateAgainstBaseline(baseline,desired);
                    if(desired.Entities.Any(e=>e.Type=="Infopoint" && !baseline.Entities.Any(b=>b.Id==e.Id)))
                        throw new InvalidDataException("Il JSON contiene i vecchi blocchi Infopoint PidBridge. Rigenerare i nuovi infopoint come MechanicalPartReference con dati BOM e targetId.");
                    progress?.Invoke("applicazione geometrie, copie e rimozioni");
                    handles=DrawingApply.Apply(db,baseline,desired);
                    db.SaveAs(stage,DwgVersion.Current);
                }
                finally{HostApplicationServices.WorkingDatabase=previous;}
            }
            if(desired.Entities.Any(e=>e.Type=="MechanicalPartReference" || e.Type=="MechanicalBalloon"))
            {
                // Mechanical COM edits require an active document, not a non-working side database.
                progress?.Invoke("apertura del documento temporaneo Mechanical");
                temporary=AcApp.DocumentManager.Open(stage,false);
                AcApp.DocumentManager.MdiActiveDocument=temporary;
                using(temporary.LockDocument())
                {
                    progress?.Invoke("creazione riferimenti parte BOM");
                    MechanicalParts.Create(temporary,desired,handles);
                    progress?.Invoke("creazione direttrici Mechanical");
                    MechanicalBalloons.Create(desired,handles);
                    progress?.Invoke("verifica direttrici e distinta prima del salvataggio");
                    MechanicalBalloons.Verify(desired);
                    MechanicalParts.VerifyBom(desired);
                    progress?.Invoke("salvataggio del nuovo DWG");
                    temporary.Database.SaveAs(output,DwgVersion.Current);written=true;
                }
                progress?.Invoke("chiusura del documento temporaneo Mechanical");
                temporary.CloseAndDiscard();temporary=null;
            }
            else{File.Move(stage,output);written=true;}
            progress?.Invoke("riapertura e verifica finale del nuovo DWG");
            var result=AcApp.DocumentManager.Open(output,false);
            AcApp.DocumentManager.MdiActiveDocument=result;
            if(desired.Entities.Any(e=>e.Type=="MechanicalPartReference" || e.Type=="MechanicalBalloon"))MechanicalParts.VerifyBom(desired);
            MechanicalBalloons.Verify(desired);
            completed=true;return result;
        }
        finally
        {
            // Cleanup must not replace the exception from geometry/BOM/save/verification.
            if(temporary!=null)
            {
                try { temporary.CloseAndDiscard(); }
                catch(System.Exception ex){DrawingFeedback.Notify("Chiusura temporaneo non completata: "+stage,ex.ToString());}
                if(previousDocument!=null)
                {
                    try { AcApp.DocumentManager.MdiActiveDocument=previousDocument; }
                    catch(System.Exception ex){DrawingFeedback.Notify("Ripristino documento precedente non completato.",ex.ToString());}
                }
            }
            try { if(File.Exists(stage))File.Delete(stage); }
            catch(System.Exception ex){DrawingFeedback.Notify("File temporaneo conservato: "+stage,ex.ToString());}
            if(!completed && written && File.Exists(output))
            {
                // If reopening/verification fails, retain the result for diagnosis; never report success.
                DrawingFeedback.Notify("\nRisultato da verificare, non convalidato: "+output);
            }
        }
    }
}
