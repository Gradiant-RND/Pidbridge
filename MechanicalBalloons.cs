using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;

namespace PidBridgeDrawing;

public static class MechanicalBalloons
{
    private static object[] Items(dynamic collection)=>((System.Collections.IEnumerable)collection).Cast<object>().ToArray();
    internal static double[] Point(Array values)=>Enumerable.Range(values.GetLowerBound(0),values.Length).Select(i=>Convert.ToDouble(values.GetValue(i))).ToArray();
    public static void Create(FullDrawing desired,Dictionary<string,string> handles)
    {
        var states=desired.Entities.Where(e=>e.Type=="MechanicalBalloon").ToArray();
        if(states.Length==0)return;
        dynamic acad=AcApp.AcadApplication;
        dynamic doc=acad.ActiveDocument;
        dynamic manager=MechanicalParts.BomManager();
        dynamic bom=manager.GetBOMTableByName("MAIN");
        object collection=bom.GetType().InvokeMember("Items",System.Reflection.BindingFlags.GetProperty,null,bom,new object[]{true});
        var bomItems=Items(collection);
        dynamic mechanical=acad.GetInterfaceObject("AcadmAuto.AcadmApplication.25");
        dynamic utility=mechanical.ActiveDocument.Utility;
        foreach(var state in states)
        {
            var spec=state.Geometry.Deserialize<MechanicalBalloonState>(DrawingProtocol.Json)!;
            dynamic source=doc.HandleToObject(spec.SourceBalloonHandle);
            if((string)source.ObjectName!="AcmBalloon")throw new InvalidDataException("La sorgente non e una direttrice numerata Mechanical: "+spec.SourceBalloonHandle);
            dynamic part=doc.HandleToObject(handles[spec.PartReferenceId]);
            var fields=MechanicalPartCommands.ReadData((Array)part.Data);
            string kks=fields.Single(d=>d.Tag.Equals("KKS",StringComparison.OrdinalIgnoreCase)).Value;
            object[] matches=bomItems.Where(o=>string.Equals((string)((dynamic)o).GetAttribute("KKS",true),kks,StringComparison.Ordinal)).ToArray();
            if(matches.Length!=1)throw new InvalidDataException("Voce BOM non univoca per la direttrice: "+kks);
            var origin=Point((Array)source.Origin);
            var sourceItems=Items((object)source.BOMItems).Select(o=>(string)((dynamic)o).GetAttribute("KKS",true)).OrderBy(s=>s).ToArray();
            dynamic balloon=source.Copy();
            // Remove both old graphic attachment and old BOM row: the source's number must not survive on the copy.
            if((bool)balloon.IsAttached)balloon.DetachGeometry();
            var oldItems=Items((object)balloon.BOMItems);
            // Mechanical erases a balloon when its last BOM item is removed.
            // Associate the destination first, then remove the inherited source rows.
            balloon.AppendBOMItem((dynamic)matches[0]);
            foreach(dynamic oldItem in oldItems)balloon.RemoveBOMItem(oldItem);
            balloon.Move(new double[]{0,0,0},spec.Offset);
            balloon.Layer=state.Layer;
            var partState=desired.Entities.Single(e=>e.Id==spec.PartReferenceId);
            var nativePart=partState.Type=="MechanicalPartReference"?partState.Geometry.Deserialize<MechanicalPartState>(DrawingProtocol.Json)!:partState.MechanicalPart!;
            dynamic target=doc.HandleToObject(handles[nativePart.TargetId??spec.PartReferenceId]);
            dynamic geometry=utility.GetObjectFromID((long)target.ObjectID,0);
            balloon.AttachGeometry(geometry,Point((Array)part.Origin));
            balloon.Update();
            if(!(bool)balloon.IsAttached || (int)balloon.LeaderCount<1)throw new InvalidDataException("Direttrice non associata alla nuova geometria: "+kks);
            var actual=Point((Array)balloon.Origin);
            if(Enumerable.Range(0,3).Any(i=>Math.Abs(actual[i]-origin[i]-spec.Offset[i])>1e-6))throw new InvalidDataException("La copia della direttrice non conserva la posizione richiesta: "+kks);
            PlaceClear((object)balloon,()=>balloon.AttachGeometry(geometry,Point((Array)part.Origin)),kks);
            var attachedItems=Items((object)balloon.BOMItems);
            if(attachedItems.Length!=1 || (string)((dynamic)attachedItems[0]).GetAttribute("KKS",true)!=kks)
                throw new InvalidDataException("Direttrice collegata alla voce BOM sbagliata: "+kks);
            handles.Add(state.Id,(string)balloon.Handle);
            if(!sourceItems.SequenceEqual(Items((object)source.BOMItems).Select(o=>(string)((dynamic)o).GetAttribute("KKS",true)).OrderBy(s=>s)))
                throw new InvalidDataException("La copia ha alterato la direttrice sorgente: "+spec.SourceBalloonHandle);
        }
    }
    private static void PlaceClear(dynamic balloon,Action attach,string kks)
    {
        var db=AcApp.DocumentManager.MdiActiveDocument.Database;
        string handle=(string)balloon.Handle;
        var id=db.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0);
        // Remeasure after attachment: Mechanical may rebuild the label and leader.
        for(int attempt=0;attempt<=3;attempt++)
        {
            LabelBox head;ObjectId owner;
            using(var tr=db.TransactionManager.StartOpenCloseTransaction())
            {
                var entity=(Entity)tr.GetObject(id,OpenMode.ForRead);
                head=BalloonObstacles.Head(entity);owner=entity.OwnerId;
            }
            var obstacles=BalloonObstacles.Read(db,owner,id);
            double gap=Math.Max(head.Height*.25,1e-6);
            (double X,double Y) shift;
            try { shift=BalloonPlacement.Find(head,obstacles,gap); }
            catch(InvalidDataException ex)
            {
                throw new InvalidDataException($"Balloon {kks} [{handle}]: {ex.Message} "
                    + $"Testa={head}; margine={gap:G17}; ostacoli={obstacles.Count}; "
                    + "ostacoli sulla testa: "+string.Join("; ",obstacles.Where(o=>head.Overlaps(o,gap))),ex);
            }
            if(shift==(0d,0d))return;
            if(attempt==3)break;
            balloon.DetachGeometry();
            balloon.Move(new double[]{0,0,0},new double[]{shift.X,shift.Y,0});
            attach();balloon.Update();
        }
        throw new InvalidDataException("Impossibile verificare un balloon libero da sovrapposizioni: "+kks);
    }
    public static void Verify(FullDrawing desired)
    {
        var states=desired.Entities.Where(e=>e.Type=="MechanicalBalloon").ToArray();
        if(states.Length==0)return;
        var expected=states.Select(s=>s.Geometry.Deserialize<MechanicalBalloonState>(DrawingProtocol.Json)!).Select(spec=>
        {
            var p=desired.Entities.Single(e=>e.Id==spec.PartReferenceId);
            return (p.Type=="MechanicalPartReference"?p.Geometry.Deserialize<MechanicalPartState>(DrawingProtocol.Json)!:p.MechanicalPart!).Data.Single(d=>d.Tag=="KKS").Value;
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var doc=AcApp.DocumentManager.MdiActiveDocument;
        using var tr=doc.Database.TransactionManager.StartOpenCloseTransaction();
        var bt=(BlockTable)tr.GetObject(doc.Database.BlockTableId,OpenMode.ForRead);
        foreach(var owner in desired.Owners.Where(o=>o.Layout!=null))
        {
            var record=(BlockTableRecord)tr.GetObject(doc.Database.GetObjectId(false,new Handle(Convert.ToInt64(owner.Id,16)),0),OpenMode.ForRead);
            foreach(ObjectId id in record)
            {
                if(tr.GetObject(id,OpenMode.ForRead) is not Entity e || e.GetRXClass().DxfName!="ACMBALLOON")continue;
                dynamic b=e.AcadObject;
                foreach(dynamic item in Items((object)b.BOMItems))
                {
                    string tag=(string)item.GetAttribute("KKS",true);
                    if(expected.Contains(tag))
                    {
                        if(!found.Add(tag) || !(bool)b.IsAttached || (int)b.LeaderCount<1 || string.IsNullOrWhiteSpace((string)item.ItemNumber))
                            throw new InvalidDataException("Direttrice BOM duplicata, non numerata o non associata: "+tag);
                        var head=BalloonObstacles.Head(e);
                        var obstacles=BalloonObstacles.Read(doc.Database,e.OwnerId,id);
                        if(obstacles.Any(o=>head.Overlaps(o,Math.Max(head.Height*.25,1e-6))))
                            throw new InvalidDataException("Balloon sovrapposto a testi o ad altri balloon: "+tag);
                    }
                }
            }
        }
        if(!expected.SetEquals(found))throw new InvalidDataException("Mancano direttrici numerate Mechanical per: "+string.Join(", ",expected.Except(found)));
    }
}
