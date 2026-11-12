using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace PidBridgeDrawing;

internal static class BalloonObstacles
{
    private static bool HasVisibleContent(Entity entity)=>entity switch
    {
        AttributeReference a when a.Invisible=>false,
        AttributeDefinition a when a.Invisible=>false,
        DBText t=>!string.IsNullOrWhiteSpace(t.TextString),
        MText t=>!string.IsNullOrWhiteSpace(t.Text),
        _=>true
    };
    private static LabelBox Box(Entity entity,Matrix3d transform)
    {
        try
        {
            var bounds=entity.GeometricExtents;bounds.TransformBy(transform);
            return new(bounds.MinPoint.X,bounds.MinPoint.Y,bounds.MaxPoint.X,bounds.MaxPoint.Y);
        }
        catch(Autodesk.AutoCAD.Runtime.Exception ex)
        {
            if(entity is DBText text && ex.ErrorStatus is (Autodesk.AutoCAD.Runtime.ErrorStatus.NullExtents
                or Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidExtents))
                return TextBox(text,transform);
            string description=entity is AttributeReference a ? "attributo "+a.Tag : entity.GetType().Name;
            throw new InvalidDataException("Impossibile leggere l'ingombro di "+description+" ["+entity.Handle+"]: "+ex.Message,ex);
        }
    }
    private static LabelBox TextBox(DBText text,Matrix3d transform)
    {
        // AttributeReference can lack cached extents despite having visible text.
        // Measure a detached DBText; never modify the original attribute or block.
        using var copy=new DBText();
        try
        {
            copy.SetDatabaseDefaults(text.Database??AcAppDatabase());
            copy.TextStyleId=text.TextStyleId;copy.Normal=text.Normal;
            copy.Height=text.Height;copy.WidthFactor=text.WidthFactor;copy.Oblique=text.Oblique;
            copy.Rotation=text.Rotation;copy.IsMirroredInX=text.IsMirroredInX;copy.IsMirroredInY=text.IsMirroredInY;
            copy.HorizontalMode=text.HorizontalMode;copy.VerticalMode=text.VerticalMode;
            copy.Position=text.Position;
            if(text.HorizontalMode!=TextHorizontalMode.TextLeft || text.VerticalMode!=TextVerticalMode.TextBase)
                copy.AlignmentPoint=text.AlignmentPoint;
            copy.TextString=text.TextString;copy.AdjustAlignment(text.Database??AcAppDatabase());
            var bounds=copy.GeometricExtents;bounds.TransformBy(transform);
            return new(bounds.MinPoint.X,bounds.MinPoint.Y,bounds.MaxPoint.X,bounds.MaxPoint.Y);
        }
        catch(Autodesk.AutoCAD.Runtime.Exception)
        {
            // Some fonts still cannot be measured in a side/temporary document.
            // Reserve a conservative envelope instead of discarding visible text.
            var position=text.Position;
            var alignment=text.HorizontalMode!=TextHorizontalMode.TextLeft || text.VerticalMode!=TextVerticalMode.TextBase
                ? text.AlignmentPoint : position;
            var box=BalloonPlacement.TextEnvelope(position.X,position.Y,alignment.X,alignment.Y,
                text.Height,text.WidthFactor,text.Oblique,text.TextString);
            double radius=Math.Max(box.Width,box.Height)/2;
            var bounds=new Extents3d(new(box.MinX,box.MinY,Math.Min(position.Z,alignment.Z)-radius),
                new(box.MaxX,box.MaxY,Math.Max(position.Z,alignment.Z)+radius));
            bounds.TransformBy(transform);
            return new(bounds.MinPoint.X,bounds.MinPoint.Y,bounds.MaxPoint.X,bounds.MaxPoint.Y);
        }
    }
    private static Database AcAppDatabase()=>Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Database;
    internal static LabelBox Head(Entity balloon)
    {
        var pieces=new DBObjectCollection();
        try
        {
            balloon.Explode(pieces);
            // Circles and number text describe the label; leader lines and arrowheads
            // must not make its footprint extend all the way to the component.
            var graphics=pieces.Cast<DBObject>().OfType<Entity>().Where(HasVisibleContent).ToArray();
            var texts=graphics.Where(e=>e is DBText or MText).Select(e=>Box(e,Matrix3d.Identity)).ToArray();
            // Open polylines are leaders; blocks may be arrowheads at the part.
            // Include only label outlines containing/overlapping its number text.
            var outlines=graphics.Where(e=>e is Circle || e is Polyline { Closed:true })
                .Select(e=>Box(e,Matrix3d.Identity))
                .Where(b=>texts.Length==0 || texts.Any(t=>b.Overlaps(t,0))).ToArray();
            var boxes=texts.Concat(outlines).ToArray();
            if(boxes.Length>0)return new(boxes.Min(b=>b.MinX),boxes.Min(b=>b.MinY),boxes.Max(b=>b.MaxX),boxes.Max(b=>b.MaxY));
        }
        catch(System.Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception
            || ex is InvalidDataException { InnerException: Autodesk.AutoCAD.Runtime.Exception })
        { /* Native custom shapes may not explode. Use conservative extents. */ }
        finally { foreach(DBObject piece in pieces)piece.Dispose();pieces.Dispose(); }
        return Box(balloon,Matrix3d.Identity);
    }
    internal static List<LabelBox> Read(Database db,ObjectId ownerId,ObjectId excluded)
    {
        using var tr=db.TransactionManager.StartOpenCloseTransaction();
        var result=new List<LabelBox>();
        void Visit(Entity entity,Matrix3d transform,int depth)
        {
            if(!entity.Visible || !HasVisibleContent(entity))return;
            if(entity is AttributeDefinition)return; // Actual attribute references are read on each insertion.
            if(entity is DBText or MText or Dimension or Table)
            {
                result.Add(Box(entity,transform));return;
            }
            if(entity.GetRXClass().DxfName=="ACMBALLOON")
            {
                var head=Head(entity);
                var bounds=new Extents3d(new(head.MinX,head.MinY,0),new(head.MaxX,head.MaxY,0));bounds.TransformBy(transform);
                result.Add(new(bounds.MinPoint.X,bounds.MinPoint.Y,bounds.MaxPoint.X,bounds.MaxPoint.Y));return;
            }
            if(entity is BlockReference block)
            {
                if(depth>=32)throw new InvalidDataException("Blocchi troppo annidati per verificare lo spazio dei balloon.");
                foreach(ObjectId id in block.AttributeCollection)Visit((Entity)tr.GetObject(id,OpenMode.ForRead),transform,depth+1);
                var definition=(BlockTableRecord)tr.GetObject(block.BlockTableRecord,OpenMode.ForRead);
                foreach(ObjectId id in definition)
                {
                    if(tr.GetObject(id,OpenMode.ForRead) is not Entity child)continue;
                    if(child is AttributeDefinition attr)
                    {
                        if(attr.Constant && HasVisibleContent(attr))result.Add(Box(attr,transform*block.BlockTransform));
                    }
                    else Visit(child,transform*block.BlockTransform,depth+1);
                }
                return;
            }
            // Mechanical custom objects can contain text that is absent from the
            // normal DBText inventory. Inspect their transient exploded graphics.
            if(entity.GetType().Name!="ImpEntity")return;
            var pieces=new DBObjectCollection();
            try
            {
                entity.Explode(pieces);
                if(pieces.Count==0)result.Add(Box(entity,transform));
                foreach(Entity piece in pieces.Cast<DBObject>().OfType<Entity>())
                    // Transient exploded blocks may expose attributes as objects,
                    // not database IDs. Reserve their complete bounds safely.
                    if(HasVisibleContent(piece) && piece is (DBText or MText or BlockReference))result.Add(Box(piece,transform));
            }
            catch(System.Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception
                || ex is InvalidDataException { InnerException: Autodesk.AutoCAD.Runtime.Exception })
            {
                // If opaque, reserve the entire object rather than assume it has no text.
                result.Add(Box(entity,transform));
            }
            finally { foreach(DBObject piece in pieces)piece.Dispose();pieces.Dispose(); }
        }
        var owner=(BlockTableRecord)tr.GetObject(ownerId,OpenMode.ForRead);
        foreach(ObjectId id in owner)
            if(id!=excluded && tr.GetObject(id,OpenMode.ForRead) is Entity entity)Visit(entity,Matrix3d.Identity,0);
        return result;
    }
}
