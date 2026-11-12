using System.IO;
namespace PidBridgeDrawing;

public static class DrawingFile
{
    public static byte[] ReadSavedDwg(string path)
    {
        // AutoCAD keeps the saved DWG open for writing; allow its existing write handle.
        var before=new FileInfo(path);long length=before.Length;DateTime modified=before.LastWriteTimeUtc;
        if(length<=0 || length>DrawingProtocol.MaxNativeBytes)throw new InvalidDataException("DWG assente, vuoto o oltre 64 MiB.");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var output=new MemoryStream((int)length);
        byte[] buffer=new byte[65536];int read;
        while((read=input.Read(buffer,0,buffer.Length))>0)
        {
            if(output.Length+read>DrawingProtocol.MaxNativeBytes)throw new InvalidDataException("DWG cresciuto oltre 64 MiB durante la lettura.");
            output.Write(buffer,0,read);
        }
        var after=new FileInfo(path);
        if(after.Length!=length || after.LastWriteTimeUtc!=modified || output.Length!=length)
            throw new InvalidDataException("Il DWG e cambiato durante la lettura: attendere il completamento del salvataggio e ripetere l'esportazione.");
        return output.ToArray();
    }
}
