using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;

[assembly:CommandClass(typeof(PidBridgeDrawing.MechanicalPartCommands))]
namespace PidBridgeDrawing;

public static class MechanicalParts
{
    internal static dynamic BomManager()
    {
        dynamic acad=AcApp.AcadApplication;
        object symbols=acad.GetInterfaceObject("SymBBAuto.McadSymbolBBMgr.25");
        return symbols.GetType().InvokeMember("BOMMgr",System.Reflection.BindingFlags.GetProperty,null,symbols,new object[]{acad.ActiveDocument})!;
    }
    public static void VerifyBom(FullDrawing desired)
    {
        dynamic manager=BomManager();
        dynamic bom=manager.GetBOMTableByName("MAIN");
        object items=bom.GetType().InvokeMember("Items",System.Reflection.BindingFlags.GetProperty,null,bom,new object[]{true});
        var rows=new List<(string Kks,int Quantity)>();
        foreach(dynamic item in (System.Collections.IEnumerable)items)
            rows.Add(((string)item.GetAttribute("KKS",true),(int)item.Quantity));
        foreach(var state in desired.Entities.Where(e=>e.Type=="MechanicalPartReference"))
        {
            var data=state.Geometry.Deserialize<MechanicalPartState>(DrawingProtocol.Json)!;
            var kks=data.Data.Single(d=>d.Tag.Equals("KKS",StringComparison.OrdinalIgnoreCase)).Value;
            var matches=rows.Where(r=>r.Kks==kks).ToArray();
            if(matches.Length!=1 || matches[0].Quantity!=data.Quantity)
                throw new InvalidDataException("Distinta MAIN: KKS/quantita non corretti per "+kks);
        }
    }
    public static MechanicalPartState? Read(Entity entity)
    {
        if(!string.Equals(entity.GetRXClass().DxfName,"ACMPARTREF",StringComparison.OrdinalIgnoreCase))return null;
        try
        {
            dynamic part=entity.AcadObject;
            var origin=(Array)part.Origin;
            return new(Enumerable.Range(origin.GetLowerBound(0),origin.Length).Select(i=>Convert.ToDouble(origin.GetValue(i))).ToArray(),
                (double)part.Scale,(int)part.Quantity,MechanicalPartCommands.ReadData((Array)part.Data),entity.Handle.ToString(),null);
        }
        catch(System.Exception ex){throw new InvalidDataException("Lettura riferimento parte Mechanical fallita ["+entity.Handle+"]. Esportazione annullata senza dati parziali.",ex);}
    }
    public static void Create(Autodesk.AutoCAD.ApplicationServices.Document document,FullDrawing desired,Dictionary<string,string> handles)
    {
        var states=desired.Entities.Where(e=>e.Type=="MechanicalPartReference").ToArray();
        if(states.Length==0)return;
        if(AcApp.DocumentManager.MdiActiveDocument!=document)throw new InvalidOperationException("Attivare il documento di destinazione Mechanical.");
        dynamic acad=AcApp.AcadApplication;
        dynamic acadDoc=acad.ActiveDocument;
        dynamic mechanical=acad.GetInterfaceObject("AcadmAuto.AcadmApplication.25");
        dynamic utility=mechanical.ActiveDocument.Utility;
        dynamic manager=BomManager();
        foreach(var state in states)
        {
            var data=state.Geometry.Deserialize<MechanicalPartState>(DrawingProtocol.Json)!;
            dynamic source=acadDoc.HandleToObject(data.SourcePartHandle);
            if((string)source.ObjectName!="AcmPartRef")throw new InvalidDataException("Sorgente non Mechanical: "+data.SourcePartHandle);
            dynamic owner=acadDoc.HandleToObject(state.OwnerId);
            dynamic part=owner.AddCustomObject("AcmPartRef");
            part.SetToDefaults();
            // Create an independent BOM component; copying a graphic or sharing the source BOM row is insufficient.
            part.SymbolStandard=source.SymbolStandard;
            part.Origin=data.Position;part.Scale=data.Scale;
            var defaults=MechanicalPartCommands.ReadData((Array)part.Data).ToDictionary(d=>d.Tag,d=>d.Value,StringComparer.OrdinalIgnoreCase);
            foreach(var field in data.Data)
            {
                if(defaults.TryGetValue(field.Tag,out var current) && current==field.Value)continue;
                try{manager.SetPartAttribute(part,field.Tag,field.Value);}
                catch(System.Exception ex){throw new InvalidDataException("Assegnazione colonna BOM "+field.Tag+" fallita per "+state.Id,ex);}
            }
            part.Quantity=data.Quantity;part.Layer=state.Layer;
            dynamic target=acadDoc.HandleToObject(handles[data.TargetId!]);
            dynamic geometry=utility.GetObjectFromID((long)target.ObjectID,0); // mcObject, from installed AcadmAuto typelib.
            part.AttachGeometry(geometry,data.Position);
            part.Update();
            if(!(bool)part.IsAttached)throw new InvalidDataException("Riferimento parte non associato alla geometria: "+state.Id);
            var actual=MechanicalPartCommands.ReadData((Array)part.Data).ToDictionary(d=>d.Tag,d=>d.Value,StringComparer.OrdinalIgnoreCase);
            foreach(var value in data.Data)
                if(!actual.TryGetValue(value.Tag,out var stored) || stored!=value.Value)
                    throw new InvalidDataException("Colonna BOM non conservata: "+state.Id+" / "+value.Tag);
            if((int)part.Quantity!=data.Quantity)throw new InvalidDataException("Quantita Mechanical non conservata: "+state.Id);
            handles.Add(state.Id,(string)part.Handle);
        }
    }
}

public sealed class MechanicalPartCommands
{
    [CommandMethod("PIDMECHDATI3",CommandFlags.Modal)]
    public void Inspect()
    {
        var doc=AcApp.DocumentManager.MdiActiveDocument;
        if(doc==null)return;
        var rows=new List<object>();
        using var tr=doc.Database.TransactionManager.StartOpenCloseTransaction();
        var bt=(BlockTable)tr.GetObject(doc.Database.BlockTableId,OpenMode.ForRead);
        var model=(BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForRead);
        foreach(ObjectId id in model)
        {
            if(tr.GetObject(id,OpenMode.ForRead) is not Entity e || e.Layer!="AM_12")continue;
            string? error=null;object? data=null,origin=null;string? name=null;
            try{dynamic p=e.AcadObject;name=(string)p.ObjectName;data=ReadData((Array)p.Data);origin=p.Origin;}
            catch(System.Exception ex){error=ex.Message;}
            rows.Add(new{handle=e.Handle.ToString(),type=e.GetType().Name,dxf=e.GetRXClass().DxfName,name,data,origin,error});
        }
        string path=Path.Combine(Path.GetDirectoryName(typeof(MechanicalPartCommands).Assembly.Location)!,"mechanical-probe.json");
        File.WriteAllText(path,JsonSerializer.Serialize(rows,DrawingProtocol.Json));
        doc.Editor.WriteMessage("\nMechanical probe: "+rows.Count+" riferimenti. "+path);
    }
    internal static AttributeState[] ReadData(Array data)
    {
        if(data.Rank!=2 || data.GetLength(1)!=2)throw new InvalidDataException("Dati Mechanical non bidimensionali.");
        return Enumerable.Range(data.GetLowerBound(0),data.GetLength(0)).Select(i=>new AttributeState(
            Convert.ToString(data.GetValue(i,data.GetLowerBound(1)))??"",Convert.ToString(data.GetValue(i,data.GetLowerBound(1)+1))??"")).ToArray();
    }
}
