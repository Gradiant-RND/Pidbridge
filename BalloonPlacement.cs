using System.IO;

namespace PidBridgeDrawing;

public readonly record struct LabelBox(double MinX,double MinY,double MaxX,double MaxY)
{
    public double Width=>MaxX-MinX;
    public double Height=>MaxY-MinY;
    public LabelBox Move(double x,double y)=>new(MinX+x,MinY+y,MaxX+x,MaxY+y);
    public bool Overlaps(LabelBox other,double gap)=>MinX<other.MaxX+gap && MaxX>other.MinX-gap
        && MinY<other.MaxY+gap && MaxY>other.MinY-gap;
}

public static class BalloonPlacement
{
    // Last resort for CAD text whose font/layout cache has no extents. Reserve
    // an intentionally generous area around both insertion and alignment anchors.
    // A square around the text radius covers rotation and mirroring in any plane.
    public static LabelBox TextEnvelope(double x,double y,double alignX,double alignY,double height,
        double widthFactor,double oblique,string text)
    {
        if(new[]{x,y,alignX,alignY,height,widthFactor,oblique}.Any(v=>!double.IsFinite(v)) || height<=0 || widthFactor<=0)
            throw new InvalidDataException("Dimensioni del testo senza ingombro non valide.");
        double radius=height*(4*Math.Max(1,text.Length)*Math.Max(1,widthFactor)+2+Math.Abs(Math.Tan(oblique)));
        if(!double.IsFinite(radius))throw new InvalidDataException("Dimensioni del testo senza ingombro troppo grandi.");
        return new(Math.Min(x,alignX)-radius,Math.Min(y,alignY)-radius,Math.Max(x,alignX)+radius,Math.Max(y,alignY)+radius);
    }
    public static (double X,double Y) Find(LabelBox head,IReadOnlyList<LabelBox> obstacles,double gap)
    {
        if(!double.IsFinite(head.MinX) || !double.IsFinite(head.MinY) || !double.IsFinite(head.MaxX)
            || !double.IsFinite(head.MaxY) || head.Width<=0 || head.Height<=0 || !double.IsFinite(gap) || gap<0)
            throw new InvalidDataException("Ingombro balloon non valido.");
        bool Free(double x,double y)=>!obstacles.Any(o=>head.Move(x,y).Overlaps(o,gap));
        if(Free(0,0))return(0,0);
        double step=Math.Max(head.Width,head.Height)/2+gap;
        // Search close to the requested position; never silently send a label far away.
        for(int ring=1;ring<=24;ring++)
        {
            var candidates=new List<(int X,int Y)>();
            for(int x=-ring;x<=ring;x++)
                for(int y=-ring;y<=ring;y++)
                    if(Math.Max(Math.Abs(x),Math.Abs(y))==ring)candidates.Add((x,y));
            foreach(var p in candidates.OrderBy(p=>p.X*p.X+p.Y*p.Y))
                if(Free(p.X*step,p.Y*step))return(p.X*step,p.Y*step);
        }
        // A free corridor may lie between grid samples. Rectangle edges define
        // every change in collision state; check those positions within the same
        // local search limit before declaring the area full.
        double limit=24*step;
        var xs=new HashSet<double>{0};var ys=new HashSet<double>{0};
        foreach(var o in obstacles)
        {
            // Round away from the obstacle so floating point cancellation cannot
            // turn a touching edge into a tiny overlap.
            double left=Math.BitDecrement(o.MinX-gap-head.MaxX);
            double right=Math.BitIncrement(o.MaxX+gap-head.MinX);
            double below=Math.BitDecrement(o.MinY-gap-head.MaxY);
            double above=Math.BitIncrement(o.MaxY+gap-head.MinY);
            if(Math.Abs(left)<=limit)xs.Add(left);
            if(Math.Abs(right)<=limit)xs.Add(right);
            if(Math.Abs(below)<=limit)ys.Add(below);
            if(Math.Abs(above)<=limit)ys.Add(above);
        }
        var orderedX=xs.OrderBy(x=>Math.Abs(x)).ToArray();
        var orderedY=ys.OrderBy(y=>Math.Abs(y)).ToArray();
        (double X,double Y)? best=null;double distance=double.PositiveInfinity;
        foreach(double x in orderedX)
        {
            if(x*x>=distance)break;
            foreach(double y in orderedY)
            {
                double candidate=x*x+y*y;
                if(candidate>=distance)break;
                if(Free(x,y)){best=(x,y);distance=candidate;break;}
            }
        }
        if(best is {} found)return found;
        throw new InvalidDataException("Nessuno spazio libero per il balloon vicino alla posizione richiesta. Spostare l'offset nel JSON.");
    }
}
