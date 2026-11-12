using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Color = Autodesk.AutoCAD.Colors.Color;

namespace PidBridgeDrawing;

public static class DrawingApply
{
    private static Point3d P(JsonElement g,string name)
    {
        var values=g.GetProperty(name).Deserialize<double[]>()!;
        if(values.Length!=3 || values.Any(v=>!double.IsFinite(v)))throw new InvalidDataException("Punto non valido: "+name);
        return new(values[0],values[1],values[2]);
    }
    private static Vector3d N(JsonElement g)
    {
        var p=P(g,"normal");var v=new Vector3d(p.X,p.Y,p.Z);
        if(v.Length<1e-12)throw new InvalidDataException("Normale nulla.");return v.GetNormal();
    }
    private static double D(JsonElement g,string key)
    {
        double v=g.GetProperty(key).GetDouble();if(!double.IsFinite(v))throw new InvalidDataException("Numero non finito: "+key);return v;
    }
    private static string S(JsonElement g,string key)=>g.GetProperty(key).GetString() ?? throw new InvalidDataException("Stringa nulla: "+key);
    private static bool B(JsonElement g,string key)=>g.GetProperty(key).GetBoolean();
    private static double Positive(JsonElement g,string key)
    {
        var value=D(g,key);if(value<=0)throw new InvalidDataException("Valore non positivo: "+key);return value;
    }
    private static ObjectId Style(Database db,Transaction tr,string name)
    {
        var styles=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
        if(!styles.Has(name))throw new InvalidDataException("Stile testo assente: "+name);return styles[name];
    }
    private static string Literal(string text)=>text.Replace("\\","\\\\").Replace("{","\\{").Replace("}","\\}").Replace("\r\n","\\P").Replace("\n","\\P").Replace("\r","\\P");
    private static void ApplyGeometry(Entity entity,DrawingEntity desired,DrawingEntity? source,Database db,Transaction tr)
    {
        if(source!=null && JsonElement.DeepEquals(source.Geometry,desired.Geometry))return;
        var g=desired.Geometry;
        switch(entity)
        {
            case AttributeDefinition:
                throw new InvalidDataException("Modifica diretta di definizioni attributo non ancora supportata: "+desired.Id);
            case Line l:
                l.StartPoint=P(g,"start");l.EndPoint=P(g,"end");l.Normal=N(g);l.Thickness=D(g,"thickness");break;
            case Polyline p:
                var vertices=g.GetProperty("vertices").EnumerateArray().ToArray();
                if(vertices.Length<2 || vertices.Length>100000)throw new InvalidDataException("Numero vertici polilinea non valido.");
                p.Normal=N(g);p.Elevation=D(g,"elevation");p.Thickness=D(g,"thickness");
                while(p.NumberOfVertices>vertices.Length)p.RemoveVertexAt(p.NumberOfVertices-1);
                var toPlane=Matrix3d.WorldToPlane(p.Normal);
                for(int i=0;i<vertices.Length;i++)
                {
                    var vertex=vertices[i];var point=P(vertex,"point").TransformBy(toPlane);
                    if(Math.Abs(point.Z-p.Elevation)>1e-5)throw new InvalidDataException("Vertice fuori dal piano polilinea.");
                    double bulge=D(vertex,"bulge"),startWidth=D(vertex,"startWidth"),endWidth=D(vertex,"endWidth");
                    if(startWidth<0 || endWidth<0)throw new InvalidDataException("Larghezza polilinea negativa.");
                    if(i>=p.NumberOfVertices)p.AddVertexAt(i,new Point2d(point.X,point.Y),bulge,startWidth,endWidth);
                    else { p.SetPointAt(i,new Point2d(point.X,point.Y));p.SetBulgeAt(i,bulge);p.SetStartWidthAt(i,startWidth);p.SetEndWidthAt(i,endWidth); }
                }
                p.Closed=B(g,"closed");break;
            case DBText t:
                t.TextStyleId=Style(db,tr,S(g,"style"));t.Normal=N(g);t.Height=Positive(g,"height");t.Rotation=D(g,"rotation");
                t.WidthFactor=Positive(g,"widthFactor");t.Oblique=D(g,"oblique");t.IsMirroredInX=B(g,"mirroredX");t.IsMirroredInY=B(g,"mirroredY");
                t.HorizontalMode=(TextHorizontalMode)g.GetProperty("horizontalMode").GetInt32();t.VerticalMode=(TextVerticalMode)g.GetProperty("verticalMode").GetInt32();
                t.Position=P(g,"position");
                if(t.HorizontalMode!=TextHorizontalMode.TextLeft || t.VerticalMode!=TextVerticalMode.TextBase)t.AlignmentPoint=P(g,"alignmentPoint");
                t.TextString=S(g,"text");t.AdjustAlignment(db);break;
            case MText t:
                t.TextStyleId=Style(db,tr,S(g,"style"));t.Normal=N(g);t.TextHeight=Positive(g,"textHeight");t.Width=D(g,"width");
                if(t.Width<0)throw new InvalidDataException("Larghezza MText negativa.");
                t.Rotation=D(g,"rotation");t.Attachment=(AttachmentPoint)g.GetProperty("attachment").GetInt32();t.Location=P(g,"position");
                t.Contents=S(g,"contents");
                break;
            case Circle c:
                c.Normal=N(g);c.Center=P(g,"center");c.Radius=Positive(g,"radius");c.Thickness=D(g,"thickness");break;
            case Arc a:
                a.Normal=N(g);a.Center=P(g,"center");a.Radius=Positive(g,"radius");a.StartAngle=D(g,"startAngle");a.EndAngle=D(g,"endAngle");a.Thickness=D(g,"thickness");break;
            case Solid s:
                var points=g.GetProperty("points").EnumerateArray().ToArray();if(points.Length!=4)throw new InvalidDataException("SOLID richiede quattro punti.");
                for(short i=0;i<4;i++) { var v=points[i].Deserialize<double[]>()!;if(v.Length!=3 || v.Any(x=>!double.IsFinite(x)))throw new InvalidDataException("Punto SOLID non valido.");s.SetPointAt(i,new Point3d(v[0],v[1],v[2])); }
                s.Normal=N(g);break;
            case BlockReference b:
                if(source==null || S(g,"name")!=S(source.Geometry,"name") || S(g,"definitionId")!=S(source.Geometry,"definitionId")
                    || B(g,"dynamic")!=B(source.Geometry,"dynamic") || B(g,"multiple")!=B(source.Geometry,"multiple"))
                    throw new InvalidDataException("Non cambiare la definizione del blocco: copiare un blocco sorgente appropriato.");
                var scale=g.GetProperty("scale").Deserialize<double[]>()!;
                if(scale.Length!=3 || scale.Any(v=>!double.IsFinite(v) || Math.Abs(v)<1e-12))throw new InvalidDataException("Scala blocco non valida.");
                var attributes=CaptureAttributes(b,tr);var before=b.BlockTransform;
                try
                {
                    b.Normal=N(g);b.Position=P(g,"position");b.Rotation=D(g,"rotation");b.ScaleFactors=new Scale3d(scale[0],scale[1],scale[2]);
                    RestoreTransformedAttributes(attributes,b.BlockTransform*before.Inverse(),tr,db);
                }
                finally{foreach(var pair in attributes)pair.Copy.Dispose();}
                break;
            default:
                throw new InvalidDataException($"geometry di {desired.Type} [{desired.Id}] e nativa: conservarla e usare transform per spostare/copiare.");
        }
    }
    private static void ApplyAppearance(Entity entity,DrawingEntity desired,DrawingEntity? source)
    {
        entity.Layer=desired.Layer;
        var a=desired.Appearance;
        if(source!=null && JsonSerializer.Serialize(a,DrawingProtocol.Json)==JsonSerializer.Serialize(source.Appearance,DrawingProtocol.Json))return;
        using var color=a.TrueColor==null ? Color.FromColorIndex(ColorMethod.ByAci,(short)a.ColorIndex)
            : Color.FromRgb((byte)a.TrueColor[0],(byte)a.TrueColor[1],(byte)a.TrueColor[2]);
        entity.Color=color;entity.Linetype=a.Linetype;entity.LinetypeScale=a.LinetypeScale;
        entity.LineWeight=(LineWeight)a.LineWeight;entity.Visible=a.Visible;
    }
    private static void ApplyAttributes(Entity entity,DrawingEntity desired,DrawingEntity? source,Transaction tr)
    {
        if(source!=null && desired.Attributes.SequenceEqual(source.Attributes))return;
        if(entity is not BlockReference block)
        {
            if(desired.Attributes.Length>0)throw new InvalidDataException("Attributi ammessi solo sui riferimenti blocco.");return;
        }
        var ids=block.AttributeCollection.Cast<ObjectId>().ToArray();
        if(ids.Length!=desired.Attributes.Length)throw new InvalidDataException("Non aggiungere o rimuovere attributi: "+desired.Id);
        for(int i=0;i<ids.Length;i++)
        {
            var attr=(AttributeReference)tr.GetObject(ids[i],OpenMode.ForWrite,false,true);
            if(attr.Tag!=desired.Attributes[i].Tag)throw new InvalidDataException("Ordine/TAG attributi cambiato: "+desired.Id);
            attr.TextString=desired.Attributes[i].Value;if(attr.IsMTextAttribute)attr.UpdateMTextAttribute();
        }
    }
    private static Entity NewEntity(string type)=>type switch
    {
        "Line"=>new Line(),"Polyline"=>new Polyline(),"DBText"=>new DBText(),"MText"=>new MText(),
        "Circle"=>new Circle(),"Arc"=>new Arc(),"Solid"=>new Solid(),
        _=>throw new InvalidDataException("Tipo nuovo non supportato: "+type)
    };
    private static List<(ObjectId Id,AttributeReference Copy)> CaptureAttributes(BlockReference block,Transaction tr)
    {
        var copies=new List<(ObjectId,AttributeReference)>();
        try
        {
            foreach(ObjectId id in block.AttributeCollection)
                copies.Add((id,(AttributeReference)tr.GetObject(id,OpenMode.ForRead).Clone()));
            return copies;
        }
        catch{foreach(var pair in copies)pair.Item2.Dispose();throw;}
    }
    private static void RestoreTransformedAttributes(List<(ObjectId Id,AttributeReference Copy)> copies,Matrix3d matrix,Transaction tr,Database db)
    {
        foreach(var pair in copies)
        {
            var expected=pair.Copy;expected.TransformBy(matrix);
            var attribute=(AttributeReference)tr.GetObject(pair.Id,OpenMode.ForWrite,false,true);
            attribute.Normal=expected.Normal;attribute.Height=expected.Height;attribute.WidthFactor=expected.WidthFactor;
            attribute.Rotation=expected.Rotation;attribute.Oblique=expected.Oblique;
            attribute.IsMirroredInX=expected.IsMirroredInX;attribute.IsMirroredInY=expected.IsMirroredInY;
            attribute.Position=expected.Position;
            if(attribute.HorizontalMode!=TextHorizontalMode.TextLeft || attribute.VerticalMode!=TextVerticalMode.TextBase)attribute.AlignmentPoint=expected.AlignmentPoint;
            if(attribute.IsMTextAttribute){using var text=expected.MTextAttribute;attribute.MTextAttribute=text;attribute.UpdateMTextAttribute();}
            attribute.AdjustAlignment(db);
        }
    }
    private static void Transform(Entity entity,Matrix3d matrix,Transaction tr,Database db)
    {
        if(entity is not BlockReference block){entity.TransformBy(matrix);return;}
        var copies=CaptureAttributes(block,tr);
        try{entity.TransformBy(matrix);RestoreTransformedAttributes(copies,matrix,tr,db);}
        finally{foreach(var pair in copies)pair.Copy.Dispose();}
    }
    public static Dictionary<string,string> Apply(Database db,FullDrawing baseline,FullDrawing desired)
    {
        DrawingProtocol.ValidateAgainstBaseline(baseline,desired);
        var originals=baseline.Entities.ToDictionary(e=>e.Id,StringComparer.OrdinalIgnoreCase);
        using var tr=db.TransactionManager.StartTransaction();
        ObjectId Id(string handle)=>db.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0);
        var resolved=originals.ToDictionary(pair=>pair.Key,pair=>Id(pair.Key),StringComparer.OrdinalIgnoreCase);
        var ownerIds=baseline.Owners.ToDictionary(o=>o.Id,o=>Id(o.Id),StringComparer.OrdinalIgnoreCase);
        var newRecords=desired.Entities.Where(e=>!originals.ContainsKey(e.Id)).ToArray();
        // Clone every group before editing/deleting originals so a copy always starts from native base state.
        foreach(var group in newRecords.Where(e=>e.SourceHandle!=null).GroupBy(e=>(e.CopyGroup,e.OwnerId)))
        {
            using var mapping=new IdMapping();
            db.DeepCloneObjects(new ObjectIdCollection(group.Select(e=>Id(e.SourceHandle!)).ToArray()),ownerIds[group.Key.OwnerId],mapping,false);
            foreach(var item in group)
            {
                var pair=mapping[Id(item.SourceHandle!)];if(!pair.IsCloned || pair.Value.IsNull)throw new InvalidDataException("Copia nativa incompleta: "+item.Id);
                resolved.Add(item.Id,pair.Value);
            }
        }
        foreach(var item in newRecords.Where(e=>e.SourceHandle==null && e.Type!="Infopoint" && e.Type!="MechanicalPartReference" && e.Type!="MechanicalBalloon"))
        {
            var owner=(BlockTableRecord)tr.GetObject(ownerIds[item.OwnerId],OpenMode.ForWrite);
            var entity=NewEntity(item.Type);entity.SetDatabaseDefaults(db);owner.AppendEntity(entity);tr.AddNewlyCreatedDBObject(entity,true);resolved.Add(item.Id,entity.ObjectId);
        }
        // Prevent transformed hatches from retaining associations with their untransformed source boundaries.
        foreach(var state in desired.Entities.Where(e=>e.Type!="Infopoint" && e.Type!="MechanicalPartReference" && e.Type!="MechanicalBalloon" && (newRecords.Contains(e) || !DrawingProtocol.IsIdentity(e.Transform))))
            if(tr.GetObject(resolved[state.Id],OpenMode.ForWrite,false,true) is Hatch hatch && hatch.Associative)hatch.Associative=false;
        foreach(var state in desired.Entities.Where(e=>e.Type!="Infopoint" && e.Type!="MechanicalPartReference" && e.Type!="MechanicalBalloon"))
        {
            DrawingEntity? source=null;
            if(state.SourceHandle!=null)source=originals[state.SourceHandle];
            bool existing=originals.TryGetValue(state.Id,out var old);
            if(existing && DrawingProtocol.SameState(old!,state))continue;
            var entity=(Entity)tr.GetObject(resolved[state.Id],OpenMode.ForWrite,false,true);
            ApplyGeometry(entity,state,source,db,tr);ApplyAppearance(entity,state,source);ApplyAttributes(entity,state,source,tr);
            if(!DrawingProtocol.IsIdentity(state.Transform))Transform(entity,new Matrix3d(state.Transform),tr,db);
            if(entity is Hatch hatch)hatch.EvaluateHatch(true);
        }
        foreach(var state in newRecords.Where(e=>e.Type=="Infopoint"))
            resolved.Add(state.Id,CreateInfopoint(db,tr,ownerIds[state.OwnerId],state));
        foreach(var state in newRecords.Where(e=>e.Type=="Infopoint"))
        {
            var info=state.Geometry.Deserialize<InfoPointState>(DrawingProtocol.Json)!;
            var entity=(Entity)tr.GetObject(resolved[state.Id],OpenMode.ForWrite);
            entity.CreateExtensionDictionary();var dictionary=(DBDictionary)tr.GetObject(entity.ExtensionDictionary,OpenMode.ForWrite);
            using var data=new ResultBuffer(new TypedValue((int)DxfCode.Text,info.Tag));
            foreach(string target in info.TargetIds)data.Add(new TypedValue((int)DxfCode.SoftPointerId,resolved[target]));
            var record=new Xrecord { Data=data };dictionary.SetAt("PIDBRIDGE_COMPONENT_LINKS_V1",record);tr.AddNewlyCreatedDBObject(record,true);
        }
        var retained=desired.Entities.Select(e=>e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var old in baseline.Entities.Where(e=>!retained.Contains(e.Id)))tr.GetObject(Id(old.Id),OpenMode.ForWrite,false,true).Erase();
        tr.Commit();
        return resolved.ToDictionary(p=>p.Key,p=>p.Value.Handle.ToString(),StringComparer.OrdinalIgnoreCase);
    }
    private static ObjectId CreateInfopoint(Database db,Transaction tr,ObjectId ownerId,DrawingEntity state)
    {
        var info=state.Geometry.Deserialize<InfoPointState>(DrawingProtocol.Json)!;
        var table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
        var definition=new BlockTableRecord { Name="PIDBRIDGE_JSON_INFO_"+Guid.NewGuid().ToString("N") };
        table.Add(definition);tr.AddNewlyCreatedDBObject(definition,true);
        Entity[] graphics=[new Circle(Point3d.Origin,Vector3d.ZAxis,1),new Line(new(-.7,-.7,0),new(.7,.7,0)),new Line(new(-.7,.7,0),new(.7,-.7,0))];
        foreach(var graphic in graphics){graphic.SetDatabaseDefaults(db);graphic.Layer="0";definition.AppendEntity(graphic);tr.AddNewlyCreatedDBObject(graphic,true);}
        foreach(var field in new[]{("KKS",info.Tag),("SOURCE_KKS",info.SourceTag),("DETAILS",info.Details),("SYSTEM",info.System)})
        {
            var attr=new AttributeDefinition { Tag=field.Item1,TextString=field.Item2,Invisible=true,Height=1 };
            attr.SetDatabaseDefaults(db);attr.Layer="0";definition.AppendEntity(attr);tr.AddNewlyCreatedDBObject(attr,true);
        }
        var owner=(BlockTableRecord)tr.GetObject(ownerId,OpenMode.ForWrite);
        var reference=new BlockReference(new Point3d(info.Position[0],info.Position[1],info.Position[2]),definition.ObjectId);reference.SetDatabaseDefaults(db);
        owner.AppendEntity(reference);tr.AddNewlyCreatedDBObject(reference,true);
        ApplyAppearance(reference,state,null);
        foreach(ObjectId id in definition)
            if(tr.GetObject(id,OpenMode.ForRead) is AttributeDefinition attr)
            {
                var value=new AttributeReference();value.SetAttributeFromBlock(attr,reference.BlockTransform);value.TextString=attr.TextString;
                reference.AttributeCollection.AppendAttribute(value);tr.AddNewlyCreatedDBObject(value,true);
            }
        if(!DrawingProtocol.IsIdentity(state.Transform))Transform(reference,new Matrix3d(state.Transform),tr,db);
        return reference.ObjectId;
    }
}
