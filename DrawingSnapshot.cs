using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace PidBridgeDrawing;

public static class DrawingSnapshot
{
    public static double[] Point(Point3d p) => [p.X,p.Y,p.Z];
    public static double[] Vector(Vector3d v) => [v.X,v.Y,v.Z];
    private static string Style(ObjectId id, Transaction tr) => ((TextStyleTableRecord)tr.GetObject(id,OpenMode.ForRead)).Name;
    public static JsonElement Geometry(Entity entity, Transaction tr)
    {
        object value = entity switch
        {
            AttributeDefinition a => new { text=a.TextString, tag=a.Tag, prompt=a.Prompt, constant=a.Constant, invisible=a.Invisible,
                position=Point(a.Position),height=a.Height,rotation=a.Rotation,normal=Vector(a.Normal),style=Style(a.TextStyleId,tr) },
            Line l => new { start=Point(l.StartPoint),end=Point(l.EndPoint),normal=Vector(l.Normal),thickness=l.Thickness },
            Polyline p => new { closed=p.Closed,normal=Vector(p.Normal),elevation=p.Elevation,thickness=p.Thickness,
                vertices=Enumerable.Range(0,p.NumberOfVertices).Select(i=>new { point=Point(p.GetPoint3dAt(i)),bulge=p.GetBulgeAt(i),startWidth=p.GetStartWidthAt(i),endWidth=p.GetEndWidthAt(i) }).ToArray() },
            DBText t => new { text=t.TextString,position=Point(t.Position),alignmentPoint=Point(t.AlignmentPoint),height=t.Height,
                rotation=t.Rotation,normal=Vector(t.Normal),widthFactor=t.WidthFactor,oblique=t.Oblique,
                horizontalMode=(int)t.HorizontalMode,verticalMode=(int)t.VerticalMode,mirroredX=t.IsMirroredInX,mirroredY=t.IsMirroredInY,style=Style(t.TextStyleId,tr) },
            MText t => new { contents=t.Contents,position=Point(t.Location),textHeight=t.TextHeight,width=t.Width,
                rotation=t.Rotation,normal=Vector(t.Normal),attachment=(int)t.Attachment,style=Style(t.TextStyleId,tr) },
            Circle c => new { center=Point(c.Center),radius=c.Radius,normal=Vector(c.Normal),thickness=c.Thickness },
            Arc a => new { center=Point(a.Center),radius=a.Radius,startAngle=a.StartAngle,endAngle=a.EndAngle,normal=Vector(a.Normal),thickness=a.Thickness },
            Solid s => new { points=Enumerable.Range(0,4).Select(i=>Point(s.GetPointAt((short)i))).ToArray(),normal=Vector(s.Normal) },
            BlockReference b => BlockGeometry(b,tr),
            Hatch h => HatchGeometry(h),
            _ => new { nativeOnly=true,nativeType=entity.GetType().Name }
        };
        return JsonSerializer.SerializeToElement(value,DrawingProtocol.Json);
    }
    private static object HatchGeometry(Hatch hatch)
    {
        string[] handles=[];
        if(hatch.Associative)handles=hatch.GetAssociatedObjectIds().Cast<ObjectId>().Select(id=>id.Handle.ToString()).ToArray();
        return new { nativeOnly=true,pattern=hatch.PatternName,patternScale=hatch.PatternScale,patternAngle=hatch.PatternAngle,
            associative=hatch.Associative,loops=hatch.NumberOfLoops,boundaryHandles=handles };
    }
    private static object BlockGeometry(BlockReference b,Transaction tr)
    {
        var definition=(BlockTableRecord)tr.GetObject(b.BlockTableRecord,OpenMode.ForRead);
        var effective=b.IsDynamicBlock ? (BlockTableRecord)tr.GetObject(b.DynamicBlockTableRecord,OpenMode.ForRead) : definition;
        return new { name=effective.Name,definitionId=definition.Handle.ToString(),position=Point(b.Position),rotation=b.Rotation,
            scale=new[] { b.ScaleFactors.X,b.ScaleFactors.Y,b.ScaleFactors.Z },normal=Vector(b.Normal),dynamic=b.IsDynamicBlock,
            multiple=b is MInsertBlock };
    }
    public static FullDrawing Read(Database db,NativeDrawing native,string originalPath,string drawingName)
    {
        using var tr=db.TransactionManager.StartOpenCloseTransaction();
        var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
        var owners=new List<OwnerState>();var entities=new List<DrawingEntity>();
        foreach(ObjectId id in bt)
        {
            var record=(BlockTableRecord)tr.GetObject(id,OpenMode.ForRead);
            string? layout=record.IsLayout ? ((Layout)tr.GetObject(record.LayoutId,OpenMode.ForRead)).LayoutName : null;
            owners.Add(new(record.Handle.ToString(),record.Name,layout,record.IsFromExternalReference || record.IsFromOverlayReference));
            foreach(ObjectId entityId in record)
            {
                if(tr.GetObject(entityId,OpenMode.ForRead) is not Entity entity)continue;
                if(entities.Count>=DrawingProtocol.MaxEntities)throw new InvalidDataException("Disegno oltre 200.000 entita: esportazione interrotta senza file parziale.");
                BoundsState? bounds=null;
                try { var box=entity.GeometricExtents;bounds=new(Point(box.MinPoint),Point(box.MaxPoint)); } catch { }
                var attributes=new List<AttributeState>();
                if(entity is BlockReference block)
                    foreach(ObjectId attrId in block.AttributeCollection)
                    {
                        var a=(AttributeReference)tr.GetObject(attrId,OpenMode.ForRead);
                        attributes.Add(new(a.Tag,a.TextString));
                    }
                using var color=entity.Color;
                entities.Add(new()
                {
                    Id=entity.Handle.ToString(),SourceHandle=entity.Handle.ToString(),OwnerId=record.Handle.ToString(),
                    Type=entity.GetType().Name,Layer=entity.Layer,Bounds=bounds,Geometry=Geometry(entity,tr),
                    Transform=DrawingProtocol.Identity(),Attributes=attributes.ToArray(),MechanicalPart=MechanicalParts.Read(entity),
                    Appearance=new(color.ColorIndex,color.ColorMethod==ColorMethod.ByColor ? new[] { (int)color.Red,color.Green,color.Blue } : null,
                        entity.Linetype,entity.LinetypeScale,(int)entity.LineWeight,entity.Visible)
                });
            }
        }
        var layerTable=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
        var layers=layerTable.Cast<ObjectId>().Select(id=>(LayerTableRecord)tr.GetObject(id,OpenMode.ForRead))
            .Select(layer=>new LayerState(layer.Name,layer.Color.ColorIndex,layer.IsLocked,layer.IsOff,layer.IsFrozen)).ToArray();
        var editableLayers=DrawingProtocol.SnapshotLayers(layers,entities);
        int nativeOnlyLayers=layers.Length-editableLayers.Length;
        return new()
        {
            Format=DrawingProtocol.Format,Version=1,DrawingId=Guid.Parse(db.FingerprintGuid).ToString("D"),DrawingName=drawingName,
            OriginalPath=originalPath,Units=db.Insunits.ToString(),Native=native,Owners=owners.ToArray(),Layers=editableLayers,Entities=entities.ToArray(),
            Notes=["JSON dell'intero DWG salvato: entita di modello, layout e definizioni blocco; dati nativi incorporati in native.",
                "Conservare native, owners e layers senza modificarli. bounds e informativo. Le risorse esterne (xref/immagini/font) rimangono esterne e devono restare disponibili.",
                "Le entita nativeOnly restano nel DWG incorporato: copiarle tramite sourceHandle e copyGroup, senza cambiarne geometry.",
                "Modificare l'elenco entities per descrivere lo stato finale: nessuna operation, nessuna selezione manuale in import.",
                "mechanicalPart contiene i dati BOM nativi dei riferimenti parte. Per nuove copie usare type=MechanicalPartReference con sourcePartHandle, data e targetId in geometry.",
                "I vecchi type=Infopoint sono blocchi PidBridge, non riferimenti parte Mechanical. Non usarli per i nuovi componenti BOM.",
                $"Record layer senza nome e senza entita associate: {nativeOnlyLayers}. Conservati nel DWG nativo incorporato, esclusi dall'inventario layers modificabile."]
        };
    }
    public static NativeDrawing Embed(byte[] bytes)=>new(Convert.ToHexString(SHA256.HashData(bytes)),Convert.ToBase64String(bytes));
    public static Database OpenEmbedded(byte[] bytes)
    {
        string temp=Path.Combine(Path.GetTempPath(),"pidbridge-base-"+Guid.NewGuid().ToString("N")+".dwg");
        var db=new Database(false,true);
        try
        {
            File.WriteAllBytes(temp,bytes);
            db.ReadDwgFile(temp,FileOpenMode.OpenForReadAndAllShare,true,null);db.CloseInput(true);
            return db;
        }
        catch { db.Dispose();throw; }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
}
