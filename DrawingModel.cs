using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PidBridgeDrawing;

public sealed record NativeDrawing(string Sha256, string Base64);
public sealed record OwnerState(string Id, string Name, string? Layout, bool External);
public sealed record LayerState(string Name, int ColorIndex, bool Locked, bool Off, bool Frozen);
public sealed record AttributeState(string Tag, string Value);
public sealed record AppearanceState(int ColorIndex, int[]? TrueColor, string Linetype, double LinetypeScale, int LineWeight, bool Visible);
public sealed record BoundsState(double[] Min, double[] Max);
public sealed class DrawingEntity
{
    public required string Id { get; set; }
    public string? SourceHandle { get; set; }
    public string? CopyGroup { get; set; }
    public required string OwnerId { get; set; }
    public required string Type { get; set; }
    public required string Layer { get; set; }
    public required AppearanceState Appearance { get; set; }
    public BoundsState? Bounds { get; set; } // Informational; recalculated by CAD, not applied.
    public required JsonElement Geometry { get; set; }
    public required double[] Transform { get; set; }
    public required AttributeState[] Attributes { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public MechanicalPartState? MechanicalPart { get; set; } // Read-only source inventory, not generic block attributes.
}
public sealed class FullDrawing
{
    public required string Format { get; set; }
    public required int Version { get; set; }
    public required string DrawingId { get; set; }
    public required string DrawingName { get; set; }
    public required string OriginalPath { get; set; }
    public required string Units { get; set; }
    public required NativeDrawing Native { get; set; }
    public required OwnerState[] Owners { get; set; }
    public required LayerState[] Layers { get; set; }
    public required DrawingEntity[] Entities { get; set; }
    public string[] Notes { get; set; } = [];
}
public sealed record InfoPointState(double[] Position, string Tag, string SourceTag, string Details, string[] TargetIds, string System);
public sealed record MechanicalPartState(double[] Position,double Scale,int Quantity,AttributeState[] Data,string? SourcePartHandle,string? TargetId);
public sealed record MechanicalBalloonState(string SourceBalloonHandle,string PartReferenceId,double[] Offset);
public sealed record DrawingDiff(int Unchanged, int Changed, int Copies, int Created, int Deleted);

public static class DrawingProtocol
{
    public const string Format = "pidbridge.full-drawing";
    public const int MaxNativeBytes = 64 * 1024 * 1024;
    public const int MaxJsonBytes = 128 * 1024 * 1024;
    public const int MaxEntities = 200000;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 48
    };
    public static double[] Identity() => [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
    public static bool IsIdentity(double[] matrix) => matrix.SequenceEqual(Identity());
    // Unused nameless records cannot be addressed by name in the editable inventory.
    // They remain intact in the embedded DWG. Keep referenced records so validation
    // still reports them rather than silently losing an entity's layer association.
    public static LayerState[] SnapshotLayers(LayerState[] layers,IEnumerable<DrawingEntity> entities)
    {
        var used=entities.Select(e=>e.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return layers.Where(l=>l==null || l.Name!="" || used.Contains(l.Name)).ToArray();
    }
    public static FullDrawing Load(string path)
    {
        if (new FileInfo(path).Length > MaxJsonBytes) throw new InvalidDataException("JSON completo oltre 128 MiB.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxJsonBytes) throw new InvalidDataException("JSON completo oltre 128 MiB.");
        if(bytes.Length>=3 && bytes[0]==0xEF && bytes[1]==0xBB && bytes[2]==0xBF)bytes=bytes[3..];
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
        CheckDuplicates(document.RootElement);
        if (!document.RootElement.TryGetProperty("format", out var format) || format.GetString() != Format)
            throw new InvalidDataException("Questo e il vecchio JSON parziale o un JSON di istruzioni. Salvare il DWG ed eseguire PIDEXPORTDISEGNO2 per il formato completo.");
        var result = JsonSerializer.Deserialize<FullDrawing>(bytes, Json) ?? throw new InvalidDataException("JSON nullo.");
        Validate(result);
        return result;
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Campo JSON duplicato: " + property.Name);
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }
    public static byte[] DecodeNative(NativeDrawing native)
    {
        if (native == null || native.Base64 == null || native.Base64.Length > (MaxNativeBytes + 2L) / 3 * 4)
            throw new InvalidDataException("Copia nativa DWG assente o troppo grande.");
        byte[] data;
        try { data = Convert.FromBase64String(native.Base64); } catch (FormatException) { throw new InvalidDataException("Copia nativa base64 non valida."); }
        if (data.Length == 0 || data.Length > MaxNativeBytes || !string.Equals(Convert.ToHexString(SHA256.HashData(data)), native.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La copia nativa DWG e stata alterata o troncata: conservare native senza modifiche.");
        return data;
    }
    public static void Validate(FullDrawing drawing)
    {
        if (drawing.Format != Format || drawing.Version != 1 || !Guid.TryParse(drawing.DrawingId, out _)) throw new InvalidDataException("Formato, versione o GUID non valido.");
        if (string.IsNullOrWhiteSpace(drawing.DrawingName) || drawing.Owners == null || drawing.Layers == null || drawing.Entities == null || drawing.Entities.Length > MaxEntities)
            throw new InvalidDataException("Disegno incompleto o oltre 200.000 entita.");
        DecodeNative(drawing.Native);
        if(drawing.Owners.Any(o=>o==null || string.IsNullOrWhiteSpace(o.Id)) || drawing.Owners.Select(o=>o.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=drawing.Owners.Length)
            throw new InvalidDataException("Owner duplicato o vuoto.");
        var layerNames = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < drawing.Layers.Length; i++)
        {
            var layer = drawing.Layers[i];
            if (layer == null || string.IsNullOrWhiteSpace(layer.Name))
                throw new InvalidDataException($"Layer vuoto alla posizione {i + 1}: nome {JsonSerializer.Serialize(layer?.Name)}."
                    + " Verificare la tabella layer con _AUDIT su una copia del DWG.");
            if (layerNames.TryGetValue(layer.Name, out int previous))
                throw new InvalidDataException($"Layer duplicato: {JsonSerializer.Serialize(layer.Name)} (posizioni {previous + 1} e {i + 1})."
                    + " Verificare la tabella layer con _AUDIT su una copia del DWG.");
            layerNames.Add(layer.Name, i);
        }
        var owners = drawing.Owners.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        var layers = drawing.Layers.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in drawing.Entities)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.Id) || !Regex.IsMatch(entity.Id, @"\A[A-Za-z0-9_-]{1,160}\z") || !ids.Add(entity.Id)) throw new InvalidDataException("Id entita vuoto, duplicato o non valido.");
            if (!owners.TryGetValue(entity.OwnerId, out var owner) || !layers.Contains(entity.Layer)) throw new InvalidDataException("Owner o layer non presente: " + entity.Id);
            // External definitions can be retained unchanged; they cannot be copied into this drawing.
            if (entity.SourceHandle != null && !Regex.IsMatch(entity.SourceHandle, @"\A[0-9A-Fa-f]{1,16}\z")) throw new InvalidDataException("sourceHandle non valido: " + entity.Id);
            if (string.IsNullOrWhiteSpace(entity.Type) || entity.Geometry.ValueKind != JsonValueKind.Object || entity.Attributes == null) throw new InvalidDataException("Stato entita incompleto: " + entity.Id);
            var m = entity.Transform;
            if (m == null || m.Length != 16 || m.Any(v => !double.IsFinite(v)) || m[12] != 0 || m[13] != 0 || m[14] != 0 || m[15] != 1)
                throw new InvalidDataException("Matrice affine 4x4 non valida: " + entity.Id);
            var determinant = m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]);
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12) throw new InvalidDataException("Trasformazione singolare: " + entity.Id);
            if (entity.Attributes.Any(a => a == null || string.IsNullOrWhiteSpace(a.Tag) || a.Value == null)) throw new InvalidDataException("Attributi non validi: " + entity.Id);
            if (entity.Appearance == null || entity.Appearance.ColorIndex is < 0 or > 256 || !double.IsFinite(entity.Appearance.LinetypeScale)
                || entity.Appearance.LinetypeScale <= 0 || string.IsNullOrWhiteSpace(entity.Appearance.Linetype)
                || entity.Appearance.TrueColor != null && (entity.Appearance.TrueColor.Length != 3 || entity.Appearance.TrueColor.Any(c=>c is < 0 or > 255)))
                throw new InvalidDataException("Aspetto grafico non valido: " + entity.Id);
        }
        foreach (var entity in drawing.Entities.Where(e => e.Type == "Infopoint"))
        {
            var info = entity.Geometry.Deserialize<InfoPointState>(Json) ?? throw new InvalidDataException("Infopoint incompleto.");
            if (info.Position == null || info.Position.Length != 3 || info.Position.Any(v => !double.IsFinite(v)) || string.IsNullOrWhiteSpace(info.Tag)
                || info.SourceTag==null || info.Details==null || info.System != "PidBridge" || info.TargetIds == null || info.TargetIds.Length == 0 || info.TargetIds.Any(id => !ids.Contains(id) || id == entity.Id))
                throw new InvalidDataException("Posizione o associazioni infopoint non valide: " + entity.Id);
        }
        foreach(var entity in drawing.Entities)
        {
            if(entity.MechanicalPart is {} inventory)ValidatePart(inventory,entity.Id,false);
            if(entity.Type=="MechanicalBalloon")
            {
                var balloon=entity.Geometry.Deserialize<MechanicalBalloonState>(Json)??throw new InvalidDataException("Direttrice Mechanical incompleta.");
                if(!long.TryParse(balloon.SourceBalloonHandle,System.Globalization.NumberStyles.HexNumber,null,out _) || balloon.Offset==null || balloon.Offset.Length!=3 || balloon.Offset.Any(v=>!double.IsFinite(v))
                    || string.IsNullOrWhiteSpace(balloon.PartReferenceId) || !ids.Contains(balloon.PartReferenceId) || balloon.PartReferenceId==entity.Id
                    || entity.SourceHandle!=null || entity.Attributes.Length!=0 || !IsIdentity(entity.Transform))
                    throw new InvalidDataException("Sorgente, spostamento o riferimento parte della direttrice non valido: "+entity.Id);
            }
            if(entity.Type!="MechanicalPartReference")continue;
            var part=entity.Geometry.Deserialize<MechanicalPartState>(Json)??throw new InvalidDataException("Riferimento parte incompleto.");
            ValidatePart(part,entity.Id,true);
            if(part.Data.Count(d=>d.Tag.Equals("KKS",StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(d.Value))!=1)
                throw new InvalidDataException("Il nuovo riferimento parte deve avere un KKS valorizzato: "+entity.Id);
            if(entity.SourceHandle!=null || entity.Attributes.Length!=0 || entity.MechanicalPart!=null || !IsIdentity(entity.Transform))
                throw new InvalidDataException("Usare geometry per i nuovi riferimenti parte, senza sourceHandle/transform/attributes: "+entity.Id);
            if(part.TargetId==null || !ids.Contains(part.TargetId) || part.TargetId==entity.Id)
                throw new InvalidDataException("Geometria associata al riferimento parte inesistente: "+entity.Id);
        }
    }
    private static void ValidatePart(MechanicalPartState part,string id,bool isNew)
    {
        if(part.Position==null || part.Position.Length!=3 || part.Position.Any(v=>!double.IsFinite(v)) || !double.IsFinite(part.Scale) || part.Scale<=0 || part.Quantity<=0
            || part.Data==null || part.Data.Length==0 || part.Data.Any(d=>d==null || string.IsNullOrWhiteSpace(d.Tag) || d.Value==null)
            || part.Data.Select(d=>d.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=part.Data.Length)
            throw new InvalidDataException("Dati riferimento parte Mechanical non validi: "+id);
        if(isNew && (string.IsNullOrWhiteSpace(part.SourcePartHandle) || !long.TryParse(part.SourcePartHandle,System.Globalization.NumberStyles.HexNumber,null,out _)))
            throw new InvalidDataException("Indicare sourcePartHandle del riferimento parte originale: "+id);
    }
    public static bool SameState(DrawingEntity a, DrawingEntity b) => a.Type == b.Type && a.OwnerId == b.OwnerId
        && a.SourceHandle == b.SourceHandle && a.Layer == b.Layer && a.Transform.SequenceEqual(b.Transform)
        && JsonSerializer.Serialize(a.Appearance,Json) == JsonSerializer.Serialize(b.Appearance,Json)
        && a.Attributes.SequenceEqual(b.Attributes) && JsonElement.DeepEquals(a.Geometry,b.Geometry);
    public static DrawingDiff Diff(FullDrawing baseline, FullDrawing desired)
    {
        var originals = baseline.Entities.ToDictionary(e => e.Id,StringComparer.OrdinalIgnoreCase);
        int unchanged=0, changed=0, copies=0, created=0;
        foreach (var entity in desired.Entities)
        {
            if (originals.TryGetValue(entity.Id,out var old)) { if (SameState(old,entity)) unchanged++; else changed++; }
            else if (entity.SourceHandle != null) copies++;
            else created++;
        }
        var ids = desired.Entities.Select(e=>e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(unchanged,changed,copies,created,baseline.Entities.Count(e=>!ids.Contains(e.Id)));
    }
    public static void ValidateAgainstBaseline(FullDrawing baseline,FullDrawing desired)
    {
        if (!Guid.TryParse(desired.DrawingId,out var id) || id!=Guid.Parse(baseline.DrawingId) || desired.Units!=baseline.Units)
            throw new InvalidDataException("Il JSON non corrisponde alla copia DWG incorporata (GUID/unita).");
        if (JsonSerializer.Serialize(desired.Owners.OrderBy(o=>o.Id),Json)!=JsonSerializer.Serialize(baseline.Owners.OrderBy(o=>o.Id),Json)
            || JsonSerializer.Serialize(desired.Layers.OrderBy(l=>l.Name),Json)!=JsonSerializer.Serialize(baseline.Layers.OrderBy(l=>l.Name),Json))
            throw new InvalidDataException("owners e layers descrivono la base nativa: conservarli senza modifiche.");
        var originals=baseline.Entities.ToDictionary(e=>e.Id,StringComparer.OrdinalIgnoreCase);
        var owners=baseline.Owners.ToDictionary(o=>o.Id,StringComparer.OrdinalIgnoreCase);
        foreach(var entity in desired.Entities)
        {
            if(originals.TryGetValue(entity.Id,out var old))
            {
                if(entity.SourceHandle!=old.SourceHandle || entity.OwnerId!=old.OwnerId || entity.Type!=old.Type)
                    throw new InvalidDataException("Non cambiare sourceHandle, ownerId o type delle entita esistenti: "+entity.Id);
                if(owners[old.OwnerId].External && !SameState(old,entity))throw new InvalidDataException("Modifica diretta di Xref non supportata: "+entity.Id);
                ValidateGeometryKeys(old,entity);
                if(entity.MechanicalPart!=null && JsonSerializer.Serialize(entity.MechanicalPart,Json)!=JsonSerializer.Serialize(old.MechanicalPart,Json))
                    throw new InvalidDataException("mechanicalPart e un inventario della sorgente: usare un nuovo MechanicalPartReference per la copia: "+entity.Id);
            }
            else
            {
                if(owners[entity.OwnerId].External)throw new InvalidDataException("Non creare entita in una Xref.");
                if(entity.SourceHandle!=null)
                {
                    if(!originals.TryGetValue(entity.SourceHandle,out var source) || entity.Type!=source.Type || owners[source.OwnerId].External)
                        throw new InvalidDataException("Sorgente di copia non valida: "+entity.Id);
                    if(string.IsNullOrWhiteSpace(entity.CopyGroup))throw new InvalidDataException("Le copie richiedono copyGroup per traslare insieme le associazioni native.");
                    ValidateGeometryKeys(source,entity);
                }
                else if(entity.Type is not ("Line" or "Polyline" or "DBText" or "MText" or "Circle" or "Arc" or "Solid" or "Infopoint" or "MechanicalPartReference" or "MechanicalBalloon"))
                    throw new InvalidDataException("Nuova entita senza sorgente non supportata: "+entity.Type);
                if(entity.Type=="MechanicalPartReference")
                {
                    var part=entity.Geometry.Deserialize<MechanicalPartState>(Json)!;
                    if(!originals.TryGetValue(part.SourcePartHandle!,out var reference) || reference.MechanicalPart==null || reference.OwnerId!=entity.OwnerId || owners[reference.OwnerId].External)
                        throw new InvalidDataException("sourcePartHandle non e un riferimento parte Mechanical esportato: "+entity.Id+". Riesportare con PIDEXPORTDISEGNO3.");
                    if(!part.Data.Select(d=>d.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(reference.MechanicalPart.Data.Select(d=>d.Tag)))
                        throw new InvalidDataException("Conservare tutte le colonne BOM del riferimento parte sorgente: "+entity.Id);
                    if(desired.Entities.Single(e=>e.Id.Equals(part.TargetId,StringComparison.OrdinalIgnoreCase)).OwnerId!=entity.OwnerId)
                        throw new InvalidDataException("Il riferimento parte e la geometria devono avere lo stesso owner: "+entity.Id);
                }
                if(entity.Type=="MechanicalBalloon")
                {
                    var balloon=entity.Geometry.Deserialize<MechanicalBalloonState>(Json)!;
                    if(!originals.TryGetValue(balloon.SourceBalloonHandle,out var source) || source.OwnerId!=entity.OwnerId || owners[source.OwnerId].External || source.Type!="ImpEntity")
                        throw new InvalidDataException("Sorgente della direttrice Mechanical non valida: "+entity.Id);
                    var part=desired.Entities.Single(e=>e.Id.Equals(balloon.PartReferenceId,StringComparison.OrdinalIgnoreCase));
                    if(part.OwnerId!=entity.OwnerId || part.Type!="MechanicalPartReference" && part.MechanicalPart==null)
                        throw new InvalidDataException("La direttrice deve indicare un riferimento parte Mechanical dello stesso owner: "+entity.Id);
                }
            }
        }
        var desiredIds=desired.Entities.Select(e=>e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedKks=baseline.Entities.Where(e=>e.MechanicalPart!=null).SelectMany(e=>e.MechanicalPart!.Data)
            .Where(d=>d.Tag.Equals("KKS",StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(d.Value)).Select(d=>d.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var item in desired.Entities.Where(e=>e.Type=="MechanicalPartReference" && !originals.ContainsKey(e.Id)))
        {
            var kks=item.Geometry.Deserialize<MechanicalPartState>(Json)!.Data.Single(d=>d.Tag.Equals("KKS",StringComparison.OrdinalIgnoreCase)).Value;
            if(!usedKks.Add(kks))throw new InvalidDataException("KKS Mechanical gia presente o ripetuto: "+kks);
        }
        if(baseline.Entities.Any(e=>owners[e.OwnerId].External && !desiredIds.Contains(e.Id)))throw new InvalidDataException("Non rimuovere entita interne a una Xref.");
        foreach(var group in desired.Entities.Where(e=>!originals.ContainsKey(e.Id) && e.SourceHandle!=null).GroupBy(e=>(e.CopyGroup,e.OwnerId)))
            if(group.Select(e=>e.SourceHandle).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=group.Count())
                throw new InvalidDataException("Una stessa sorgente e ripetuta nel copyGroup: "+group.Key.CopyGroup);
    }
    private static void ValidateGeometryKeys(DrawingEntity original,DrawingEntity desired)
    {
        var oldKeys=original.Geometry.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
        if(!oldKeys.SetEquals(desired.Geometry.EnumerateObject().Select(p=>p.Name)))
            throw new InvalidDataException("Campi geometry mancanti o sconosciuti: "+desired.Id);
        if(desired.Attributes.Length!=original.Attributes.Length || !desired.Attributes.Select(a=>a.Tag).SequenceEqual(original.Attributes.Select(a=>a.Tag)))
            throw new InvalidDataException("Non aggiungere, rimuovere o riordinare i TAG attributo: "+desired.Id);
    }
}
